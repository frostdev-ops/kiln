# Creature AI and behaviour: who it fights, how it moves, what it does

An `AIBlock` says how a creature notices, chases, wanders and runs, and who it treats as an enemy. A `BehaviourBlock`'s rules say what it does on
top of the game's combat. Both are parts of a [`CreatureBlock`](npc-blocks.md). Everything here is server side: vanilla players watch the result
(`clients: none` on a game body), and nothing runs on the game's job threads that a mod wrote.

```csharp
Blocks.Define("warden", new CreatureBlock
{
    Body = Bodies.Vukah,
    AI = AIs.Vukah with
    {
        Sight = 30, NightSight = 0.5,
        Flee = new FleeBlock { Below = 0.25, Min = 12, Max = 24, Seconds = 6 },
        Relations = new Dictionary<Faction, Relation> { [Faction.Player] = Relation.Neutral, [Faction.Scavenger] = Relation.Hostile },   // its own view
        SenseFilter = (npc, other) => !Friends.Contains(other),   // one creature or player at a time; Friends is your own set
    },
    Behaviour = Behaviours.VukahMelee with
    {
        Rules =
        [
            // below half health, every 10 s, 30 % of the time: call two helpers and heal a little
            new RuleBlock { Every = 10, Chance = 0.3, When = [Is.HealthBelow(0.5), Is.SummonsBelow(2)], Do = [Act.Summon("Enemy_Vukah_Melee", 2), Act.Heal(0.1)] },
            // in a fight, now and then: throw ice at its target
            new RuleBlock { Every = 1, Cooldown = 4, When = [Is.InCombat, Is.HasTarget], Do = [Act.Cast(new ProjectileBlock { Speed = 25, Range = 30, OnHit = [frost] })] },
        ],
    },
});
```

## AIBlock
Starts from a game creature's tuning (`AIs.Vukah`, `AIs.Scavenger`, `AIs.Heavy`, `AIs.Still`): hundreds of the game's fields have no neutral value.
Every creature of the block gets its own copy of the values (kiln.creatures' private copies), so the game's other creatures stay as they are.

| Field | What it does | Game field |
|---|---|---|
| `Sight`, `SightAngle`, `Aggro`, `Leash` | See [npc-blocks.md](npc-blocks.md#ai) | `combatCommitmentSetup.view.*`, `homeZoneSetup.radius` |
| `NightSight` | Share of `Sight` at night, 0..4 (the Vukah's 0.6) | `combatCommitmentSetup.view.nightRangeFactor` |
| `Hears`, `Hearing` | Reacts to noise at all; share of the game's noise ranges | `noiseListenerSetup.isEnabled`, `listenRangeFactor` |
| `Threat` | How much it draws enemies' attention, 1 as the game has it | `threatFactor` |
| `Roam { Min, Max, FromHome }` | Wanders while idle; turns the game's Roaming on | `roamingSetup.*` |
| `Flee { Below, Min, Max, Seconds, Cooldown }` | Runs from a fight below a share of its health (a rule Kiln adds to its arsenal), as far and as long as said | `fleeSetup.*`, the game's Flee state |
| `ReturnsHome = false` | Never walks home after a fight | features ReturnHome, FleeHome off |
| `CallsForHelp`, `HelpRadius` | Calls the friends around it into its fights | feature CallForHelp, `callForHelpSetup.radius` |
| `Enable`, `Disable` | Any of the game's feature switches (`EnemyFeatures.Prowl \| EnemyFeatures.Circling`) | `enabledFeatures` |
| `Faction` | Its side | `Faction` |
| `Relations` | Its own view of other factions (below) | kiln.npcai |
| `SenseFilter`, `SenseEvery` | Its view of single creatures and players (below) | kiln.npcai |
| `PickTarget`, `PickEvery` | Your code picks its target | kiln.creatures |
| `Tune` | Any other field by path; wins over the fields above | `Enemy01BehaviorState.*` |

### Who fights whom
The game decides it in five small functions over the factions (`keen::FactionType`): "would a fight b" (enemy targeting's candidate check and the
sensors ask it) and "is b on a's side". Equal factions and the players' side (Player, AutomatedPlayer, PlayerAlly) are friends; most others fight; a
few species leave each other alone (Vukahs and wild beasts). kiln.npcai answers in their place, from three layers, most specific first:

1. **`AIBlock.SenseFilter`**: one creature about one other creature or player. `(npc, other) => bool` runs on the tick thread every `SenseEvery`
   seconds (0.5) for up to 256 candidates within 1.25 × max(sight, 8 metres); false makes it ignore that one (neutral) until asked again. The game's targeting reads the
   answer. Calls are posted through the owning mod's scheduler; no managed delegate runs in a native faction hook. An exception disables that creature's filter and clears its answers. In JSON a name: `"senseFilter": "tokens"` with `Blocks.SenseFilter("tokens", (npc, other) => ...)` in `OnLoad`.
2. **`AIBlock.Relations`**: one creature about a faction. `{ [Faction.Vukah] = Relation.Hostile }` makes a Vukah-bodied creature hunt Vukahs.
3. **`Factions.Relate(a, b, relation, mutual: true)`**: the world's table, for every creature of faction a. `Factions.Reset()` gives back what
   your mod set; it all goes when the mod unloads. Two mods on one pair: the last set wins.

`Relation.Hostile` fights, `Friendly` is on its side (it helps, it does not fight), `Neutral` ignores, `Game` is the game's answer. A creature's
relations and filter are **its own view**: a Scavenger still fights a creature that is neutral to Scavengers, unless the world's table or the
Scavenger's own block says otherwise. `Factions.Between(a, b)` answers what the game's checks say now; `Factions.Fights(a, b)` and `Friends` by faction;
`Factions.Status` counts what Kiln answered.

The five faction hooks go in at the next Pre with the first effective relation and come out at the next Pre after the last is cleared or expires.
kiln.hooks keeps every link slot for the process, so after 64 such in-and-out rounds the hooks stay in through quiet spells and simply answer nothing.
Rows and pairs apply to pointer forms, which carry the creatures' Faction component addresses; value forms see only the world table.
World lifecycle support must be available, and all five checks must resolve and install; a missing pattern or failed installation leaves the game's answers in place, with a log line. A failed
installation is not retried each tick. Revoked links stay allocated until process exit (kiln.hooks never frees one), so concurrent calls can finish safely.
Per-creature overrides also require an entity-destruction observer. When it is unavailable those overrides are refused. The observer invalidates
lifetime tokens before storage is reused (a refused destroy conservatively drops overrides too). World stops clear entity state and the world table; register persistent host-world relations from `ServerStartedEvent`. Native workers
read retained immutable snapshots and atomic table cells. A creature's row and pair changes reach the hooks at the next Pre. A friendly fighter is a creature block with `Faction = Faction.PlayerAlly` (an `NpcBlock` villager has no
combat AI to change).

## BehaviourBlock rules
`RuleBlock { On, Every, Cooldown, Chance, When, Do }` sits in the game's own rule list (arsenal data): the game's rule walker decides when, a rule
can mix the game's kinds and Kiln's, and an unknown kind is ignored safely. Three kinds of condition and action, cheapest first:

**The game's own kinds** (data, no hook):

| C# | JSON | Game type |
|---|---|---|
| `Is.HealthBelow(f)`, `Is.HealthAbove(f)` | `{ "healthBelow": 0.5 }` | `EnemyHealthPercentageConstrain` |
| `Is.TargetWithin(m)`, `TargetBeyond`, `TargetBetween` | `{ "targetWithin": 4 }` | `DistanceConstrain` |
| `Is.InCombat`, `Is.InState(EnemyState...)` | `{ "inCombat": true }`, `{ "state": ["Combat"] }` | `StateConstrain` |
| `Is.HasTarget` | `{ "hasTarget": true }` | `HasCombatTargetConstrain` |
| `Is.TargetIsPlayer(bool)` | `{ "targetIsPlayer": true }` | `IsPlayerTargetConstrain` |
| `Is.TargetHealthBelow(f)` | `{ "targetHealthBelow": 0.3 }` | `CurrentTargetHealthPercentageConstrain` |
| `Is.HitWithin(s)` | `{ "hitWithin": 2 }` | `LastHitReceivedConstrain` |
| `Is.Chance(p)` (and the rule's `Chance`) | `{ "chance": 0.3 }` | `RandomChanceConstrain` |
| `Is.TargetAtHome` | `{ "targetAtHome": true }` | `IsTargetInHomeZoneConstraint` |
| `Act.Flee()` | `{ "flee": true }` | `TriggerStateConditionAction` WantsToFlee |
| `Act.BackOff(min, max)` | `{ "backOff": [3, 6] }` | `RunAwayAction` |
| `Act.Play(sequence)` | `{ "play": "<guid>" }` | `PlayActionSequenceAction` |
| `Act.Immune(bool)` | `{ "immune": true }` | `SetImmuneAction` |
| `Act.Pause(min, max)` | `{ "pause": [1, 2] }` | `PauseAction` |
| `Act.EnableFeature(f)`, `DisableFeature` | `{ "enableFeature": "Prowl" }` | `EnableFeatureAction` |
| `Act.SetPhase`, `Act.Attack`, `Is.InPhase`, `Is.Night` ... | see [npc-blocks.md](npc-blocks.md#behaviour) | |
| anything else | `Is.Game("Type", ...)`, `{ "type": "...", ... }` | the game's 50 kinds |

**Kiln's conditions that read the world**: code on the tick thread every half second, published to the table the game's job threads read:
`Is.EnemiesNear(count, metres)` (`{ "enemiesNear": [2, 10] }`, by the game's own check with Kiln's relations in it), `Is.AlliesNear(count, metres)`,
`Is.SummonsBelow(n)` (`{ "summonsBelow": 2 }`).

**Kiln's actions that change the world**: queued when the rule fires, run on the tick thread in the next step:
- `Act.Summon(creature, count, radius, seconds, side)`: a creature block (`"imp"`, `"mod:imp"`), a variant or a game creature, around it, on its side
  and after its target; `seconds` > 0 removes them after that. `{ "summon": { "creature": "imp", "count": 2, "radius": 3, "seconds": 30 } }`.
- `Act.Cast(delivery)`: a delivery (a projectile ...) from where it stands at its target, without an animation; compiled with the creature.
  `{ "cast": { "type": "projectile", "speed": 25, "range": 30, "onHit": [ ... ] } }`. For a cast with an animation, give it an attack with that
  `Delivery` and `Act.Attack`.
- `Act.Heal(fraction)`: `{ "heal": 0.1 }`. A fraction of zero does nothing.
- `Act.Apply(status, on)`: `{ "apply": "chilled", "on": "self" }`; `Act.Effect(effect, on)` for any effect.
- `Act.Custom(npc => ...)`: anything, with the whole API.

Queued actions are limited to 4 per creature and 64 globally per tick; excess work is dropped. Summons reserve capacity before awaiting a spawn:
at most 16 living or pending summons per summoner, 128 per mod, and 256 globally; at most 16 starts per tick. Summons may call helpers through three
generations. Summoning also requires the native lifetime observer; without it the action logs a refusal and skips the spawn. Failed spawns, deaths and unloads release capacity. These limits apply even without an `Is.SummonsBelow` condition. Summon radius is
0..200 metres and lifetime is 0..86400 seconds (finite). Use rule intervals and cooldowns for predictable pacing.

The shared kiln.creatures action queue captures entity and target lifetime tokens when a rule fires. Destruction invalidates the tokens before
an ID or address can be reused; the drain drops stale actions with a debug log. Managed callbacks drain one action at a time, so an earlier action
cannot redirect later work onto a replacement. World stops and unload discard pending work. If the lifetime observer is unavailable on a build,
the queue keeps its previous ID-based behaviour and warns once.

## Mistakes, refused by name
`block creature warden, ai.flee.below: 2 is outside 0..1`; `ai.senseFilter: no sense filter "tokens": register it with Blocks.SenseFilter(...)`;
`behaviour.rules[0].do[0].summon: count 99 is outside 1..16`; `behaviour.rules[1].do[0].cast.onImpact[0]: this place has no target of its own ...`;
`behaviour.rules[0].when[0].chance: 7 is outside 0..1`. Each JSON condition or action names one kind; extra keys, null payloads, fractional counts, unknown states/features and reversed min/max pairs
are refused with the block and field. JSON property order does not matter. `kiln lint` checks the AI and rule shapes against the schema.

## Costs
Faction hooks retain a snapshot for each pointer check and read atomic table cells. With no effective relations the five hooks are taken out (after 64 rounds of that they stay in and answer nothing).
The destruction observer remains while lifetime tokens are tracked. A SenseFilter is tick-thread work: keep it short and avoid blocking.
Summons, casts, heals and status effects also run on the tick thread. The older timings in [npc-internals.md](../../re/npc-internals.md) 3.6 predate
snapshot lifetime protection and are not measurements of the current implementation.
