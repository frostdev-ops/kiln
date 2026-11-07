# Wiki note: creature AI and behaviour (agent npcai, 2026-11-07)

What changed for the public guide (frostdev.io/kiln/wiki). The full mod-author page is `docs/kiln/modding/npc-ai.md`; fold its first two sections
into the guide's "Creatures" chapter, after "Behaviour".

## New: who fights whom
Suggested text:

> **Who fights whom.** The game decides whether one creature fights another by faction. Kiln lets you change that three ways, most specific first:
> a creature's **SenseFilter** (`(npc, other) => bool`, for single creatures and players), its own **Relations** (`{ [Faction.Player] =
> Relation.Neutral }`), and the world's table (`Factions.Relate(Faction.Vukah, Faction.Scavenger, Relation.Hostile)`). Relations are `Hostile`,
> `Friendly`, `Neutral` (ignore) or `Game`. A creature's relations are its own view: the others still see it their way. It all runs on the server, so
> players without Kiln watch the fights. A friendly fighter is a creature with `Faction = Faction.PlayerAlly`.

## New AI fields
`NightSight`, `Hears`, `Hearing`, `Threat`, `Roam { Min, Max, FromHome }`, `Flee { Below, Min, Max, Seconds }`, `ReturnsHome`, `CallsForHelp`,
`HelpRadius`, `Enable` / `Disable` (the game's feature switches), `Relations`, `SenseFilter`, `SenseEvery`. Each creature gets its own copy of the
values. Same names in `blocks/*.json` (camelCase).

## New rule conditions and actions
Suggested text:

> A rule can say "below half health, every 10 s, 30 % of the time: summon two helpers" in one line:
> `new RuleBlock { Every = 10, Chance = 0.3, When = [Is.HealthBelow(0.5), Is.SummonsBelow(2)], Do = [Act.Summon("Enemy_Vukah_Melee", 2)] }`.

- The game's own kinds, as data: `Is.InCombat`, `Is.InState`, `Is.HasTarget`, `Is.TargetIsPlayer`, `Is.TargetHealthBelow`, `Is.HitWithin`,
  `Is.Chance`, `Is.TargetAtHome`; `Act.Flee`, `Act.BackOff`, `Act.Play`, `Act.Immune`, `Act.Pause`, `Act.EnableFeature`, `Act.DisableFeature`.
- Kiln's, reading the world on the tick: `Is.EnemiesNear(n, m)`, `Is.AlliesNear(n, m)`, `Is.SummonsBelow(n)`.
- Kiln's, changing the world on the tick: `Act.Summon`, `Act.Cast(delivery)` (a projectile at its target, no animation), `Act.Heal(fraction)`,
  `Act.Apply(status)`.
- JSON forms: `{ "summonsBelow": 2 }`, `{ "summon": { "creature": "...", "count": 2 } }`, `{ "cast": { "type": "projectile", ... } }`,
  `{ "flee": true }`, ... (table in npc-ai.md).

## Sample
`mods/samples/CreatureAi`: a world feud (Vukahs and Scavengers), a warden that ignores players and hunts Scavengers, a caller (JSON) that summons
and throws ice, a ranger on the players' side with a sense filter.

## Status line for the guide
Server side, proven in a lab without players (creatures find, ignore and hunt each other by the game's own targeting with Kiln's answers in it;
rules summon, cast, heal). Not yet watched in the real game: see the human checklist in the npcai report / `docs/kiln/building-blocks-plan.md`.
