# Creature and NPC blocks

A creature is four parts: a **body** (what it is), an **animation** (how it moves), an **AI** (how it notices and chases) and a **behaviour** (what
it does). Each part is a block you can define once and reuse, or write inline. The premades (`Bodies.Vukah`, `Animations.Vukah`, `AIs.Vukah`,
`Behaviours.VukahMelee`) are plain instances of the same blocks. An NPC is a body with a conversation, wares and a place in the roster.

Sample: `mods/samples/CreatureBlocks`, a creature made entirely of its own files (a script writes its glTF), a Vukah with a rule of its own, a creature
driven by C#, and a wandering trader.

```csharp
var crush = Blocks.Define("crush", new EffectBlock { Damage = Damage.Blunt(35), Knockback = Knockback.Small });

Blocks.Define("stoneling", new CreatureBlock
{
    Name = "Stoneling",
    Body = "stone-body",                    // blocks/stoneling.json: a model of your own
    Animation = "stone-moves",              // and its clips
    AI = AIs.Vukah with { Sight = 24, Faction = Faction.Vukah },
    Behaviour = new BehaviourBlock
    {
        Attacks = [new AttackBlock { Name = "slam", Plays = "slam", Range = 2.8, HitAt = 0.55, OnHit = [crush] }],
        Rules = [new RuleBlock { Every = 1, When = [Is.HealthBelow(0.5)], Do = [Act.SetPhase("cracked"), Act.Effect(crush)] }],
    },
    Stats = new CreatureStats { Level = 12 },
    Spawns = [new SpawnBlock { Replaces = "Enemy_Vukah_Melee", Chance = 0.1, NightOnly = true }],
});
```
```jsonc
{ "format": 1,
  "animations": { "stone-moves": { "clips": "models/stoneling.glb", "idle": "Idle", "walk": "Walk", "run": "Run", "dead": "Dead",
                                   "hit": { "clip": "Flinch", "reference": "Ref" }, "states": { "slam": { "clip": "Attack" } } } },
  "bodies": { "stone-body": { "model": "models/stoneling.glb", "slots": { "HandR": "hand_r" }, "size": { "radius": 0.5, "height": 2.2 } } },
  "creatures": { "stoneling": { "name": "Stoneling", "body": "stone-body", "animation": "stone-moves",
                                "ai": { "like": "vukah", "sight": 24, "faction": "Vukah" },
                                "behaviour": { "attacks": [ { "name": "slam", "plays": "slam", "range": 2.8, "hitAt": 0.55, "onHit": [ "crush" ] } ] } } } }
```
JSON sections: `creatures`, `npcs`, and for parts `bodies`, `animations`, `ais`, `behaviours`, `attacks`, `rules`. A JSON part starts from a premade with
`"like"`; in C# that is `AIs.Vukah with { … }`.

## Two kinds of creature
| Body and animation | What it is | `clients` |
|---|---|---|
| The game's (`Bodies.Vukah`, the default animation) | The game's creature with a new mind: a variant of it, with your AI values, attacks and rules | `none`; `kiln` once it has attacks of its own (a new sequence) |
| Yours (`Model`, `Clips`) | A new template: the AI's creature, playing your graph and drawing your model | `mod`: every player's game needs the files at start |

The build works out `clients` from the blocks: C# blocks through the analyzer, data files through the manifest task.

## Bodies and animations with files of their own
A body names a skinned glTF of the mod (`Model`), an animation a glTF whose animations are its clips (`Clips`); one file can be both. They are
**cooked when the mod builds** (`kiln build`, or `kiln cook` in the mod's folder): the cook reads `blocks/*.json` itself, so `assets/cook.json` needs no
steps (`{ "format": 1, "tool": "animcook", "steps": [] }`). It makes the skeleton, the clips, a state machine graph per animation, and the model
([animations.md](animations.md), [models.md](models.md#skinned-models)).

**A body or animation with files is written in a data file.** The cook does not run your code, so it cannot see one written in C#; `Define` refuses
such a block, says where it belongs, and C# names the data file's block instead. Everything else (AI, behaviour, the creature itself) can be in either.

### Animation
| Field | What |
|---|---|
| `Idle` | Needed: the clip it stands in |
| `Walk`, `Run` | Needed when the AI moves it. `Blends.ByDirection("WalkF", "WalkB")` blends by the direction it walks |
| `Sprint`, `IdleCombat`, `Fall`, `IdleSneak`, `Sneak`, `Stun` | The engine's other states. `IdleCombat` and `Fall` stand as in Idle when left out |
| `Dead` | Plays when it dies and holds its last frame |
| `Hit` | Plays over everything when it is hit (the engine's hit overlay). With `Reference` it adds to what plays |
| `Stagger` | What a hit reaction, a stun or a parry plays over the whole body. Default: `Hit`'s clip once, else Idle. `Stun` defaults to it, `Dead` to Idle, held |
| `States` | Your own: `[new AnimState("slam")] = new Clip("Attack") { Speed = 1.2 }`, in JSON `"slam": { "clip": "Attack", "speed": 1.2 }`. `Then` moves on to another state when the clip ends |
| `Layers` | More clips over the states, always or on an overlay |
| `Skeleton` | The skeleton's name, the file's name by default: a body and an animation share a skeleton when they name the same one |

A clip is a glTF animation's name. A state machine takes at most 34 transitions: the cook nests machines past that, and you list states.

### Body
`Model`, `Skeleton`, `Scale`, `Slots` (the game's attachment slots on your joints: where gear hangs and where hits come from), `Size` (radius and height,
for paths) and `Colliders` (a capsule or sphere for hits and one for what it bumps into; one shape is both). Its bounds for culling are the model's.

## What Kiln checks, and why
The game checks none of this; a mistake shows as a creature frozen in a pose, or a blow that never lands. So `Define` refuses, naming block and field:

| Rule | Because |
|---|---|
| A body of your own and its animation share a skeleton, and the cooked model and graph name the same one | The game poses a model only by the skeleton its graph names: otherwise it draws unposed |
| An attack's `Plays` is a state of the animation | A sequence that sets a state the graph lacks plays nothing, and the hit that follows the clip never comes |
| `Plays` is a state of yours, or an engine state with a known GUID (`Run` has none) | A sequence names a state by GUID |
| A creature that moves has `Walk` and `Run` | The engine asks for them by id while it walks |
| A behaviour that keeps a game creature's attacks (`Behaviours.VukahMelee`) needs that creature's animation | Its sequences play that creature's states |
| A game body plays its own graph, a body of yours an animation of yours | The skeleton again |
| A rule's phase or attack is one of the behaviour's | A typo would be a phase nothing enters |

What a body of your own gets from the AI's creature, and what Kiln changes so it can't disagree with your animation:
- the AI's fields that name a state (combat idle, patrol, flee, sleep, swimming) point at your Idle, IdleCombat and Walk;
- its hit reactions, stuns, parries and death are sequences of its own: copies of the base creature's whose state events play your `Stagger` (or
  `Dead` for a death), keeping an engine state your graph has (Stun, Fall, Dead). They are made when content is ready, from the running game's data;
- the base creature's other motions that are sequences of its states (taunt, blocking, call for help, idle turns, sleep, its spawn sequence) are off;
- its gear (`SubEntities`) is left out.

## Behaviour
**Rules first.** A creature keeps the game's combat: blocking, flanking, hit reactions, choosing among its attacks. You add attacks and rules.

- `AttackBlock`: `Plays` a state, `Range`, `MinRange`, `Cooldown`, `Style`, `Chance`, `HitAt`, `OnHit` (what the blow does to everything in front within
  range, through `Angle` degrees) or a `Delivery` (a projectile, a cone, any of the spells' deliveries) for a ranged attack. Kiln makes its sequence: the
  attack's state from the start, the blow at `HitAt`, turning to the target before it. On a game body, `Sequence` plays one of the game's as it is.
- `RuleBlock`: `On` (when it is looked at), `Every`, `Cooldown`, `Chance`, `When` (all hold), `Do`. Conditions and actions are kiln.creatures'
  ([creatures.md](../services/creatures.md#rules-and-arsenals)): the game's kinds (`Is.HealthBelow`, `Is.TargetWithin`, `Is.Game(...)`, `Act.Game(...)`),
  Kiln's (`Is.InPhase`, `Is.Night`, `Act.SetPhase`, `Act.Attack`), your code (`Is.Custom`, `Act.Custom`), and `Act.Effect(effect)`: an effect block on the
  creature's target, from the creature, or `NpcEffectOn.Self` on itself.
- **Your code never runs on a job thread.** `Is.Custom` runs on the tick thread every `Every` seconds and the game reads its last answer; `Act.Custom`
  is queued and runs at the next step. In JSON: `{ "custom": "name" }`, with `Blocks.Condition("name", npc => …)` and `Blocks.Action(...)` in `OnLoad`.
- `PickAttack` (an attack's name, or null for the game's choice) and `AIBlock.PickTarget` replace one decision. In JSON `"pickAttack": "name"` with
  `Blocks.AttackPicker`, `"pickTarget"` with `Blocks.Target`.
- `Phases`: the phases its rules may name.

**`Brain`, opt-in per creature**: `Brain = () => new MyBrain()`, in JSON `"brain": "name"` with `Blocks.Brain("name", () => new MyBrain())`. The game's AI
is off for that creature, and with it the game's perception: `NpcControl` sees for it (`Seen`, enemies within the AI's `Sight` by the game's faction
rule; `Target`, the nearest). `MoveTo`, `Stop`, `Face`, `Attack(name)`, `Pose(state)`, `Effect(...)`; `Creature` for its values. `Tick` runs on the tick
thread every step. You lose what the game's combat states did; a creature still takes hits and plays its sequences.

```csharp
sealed class Sentinel : NpcBrain
{
    public override void Tick(NpcControl npc)
    {
        if (npc.Target.IsNone) { npc.Stop(); return; }
        if (npc.TargetDistance < 2.8) npc.Attack("slam");
        else npc.MoveTo(World.Position(npc.Target)!.Value, run: true);
    }
}
```

## AI
`Sight`, `SightAngle`, `Aggro`, `Leash` (its home zone's radius), `Faction`, `Moves`, `Fights`, `PickTarget`, and `Tune` for any other field of the game's AI
by path (`["combatSetup.preferedDistance"] = "3"`). It starts from a game creature's tuning (`Template`, the Vukah's by default), because hundreds of fields
have no neutral value. A body of your own runs that creature's AI program; a friendly fighter is a creature with `Faction = Faction.PlayerAlly`.

## Server and client
A creature's arsenal, variant and spawn rules belong to the server world. A server writes them as the mod loads. A game client loads mods off the tick
thread and has a server world only while it hosts one, so there they are written at the first step of a hosted world, before anything of it spawns; a
client joined to another server never needs them. What a block can't take then is an error of the mod by block and field, as in a data file.
Brains are the server's too: they run on the tick thread of a server world (a dedicated server's, or the one a client hosts), never in a client's
menu or in its world joined to another server.

## Spawning
`Blocks.Spawn("stoneling", position)` or the handle's `Spawn`; `/creature spawn <mod>:<name>` takes the variant. `Spawns` puts it in place of the game's
creatures of a template, by chance, at night or by day.

## NPCs
```csharp
Blocks.Define("tobin", new NpcBlock
{
    Name = "Tobin the Wanderer",
    Body = Bodies.Fisher,
    Talk = tobinDialogue,                                    // a DialogueDefinition, or TalkName for one of quests/*.json
    Vendor = new VendorBlock { Sells = { [GameItems.Material_T1_Plant_Fibre] = Price.Of(GameItems.Material_T1_Twigs, 2) } },
    Roster = new RosterBlock { SummonTool = "Tobin's Whistle", Description = "Tobin trades a little." },
    Place = new NpcPlace { Distance = [6, 10] },
});
```
It becomes the quests' NPC ([npcs.md](../services/npcs.md)): a new NPC of its own, cloned from the body's game NPC, with its dialogue, so quests name it as a
`giver` or in `talk`. Its wares are recipes at the body's workshop, which the game's NPC of that workshop shows too. **Its roster entry and lines are built
by the game at start**:
- an NPC block in `blocks/*.json` is read by that start-up build from the mod's folder, so it is there at the **first start**, on a server and on each
  player's game;
- one written in C# is known only once the mod's code runs, so Kiln writes it to `kiln/data/kiln-quests/native/<mod>.blocks.json` (the data folder: a
  redeploy keeps it, and nothing is written into the mod) and it is there from the next start. Write NPCs in data files to have them at once.

`Place` puts it once per world: `At` a spot, or `Distance` metres from the first player in game. A base is fine (`InBase`, default true), so it comes
that near while the player is home; `InBase = false` keeps it 20 m outside every base zone, as a quest's places are. `Exact` takes `At` as it is. The
world remembers the spot, so a change of `Place` shows in a new world. It then walks about its spot within the roster's `RoamingRange`, as the game's
villagers do (the body's own when not set).

A `quests/*.json` of the mod's own that names the same NPC wins over both. Players need the mod.

An NPC's body is a game NPC's for now (`Bodies.Villager`, `Bodies.Fisher`): the start-up build would also have to set a custom body's graph and model, and
its portrait in the roster is unseen.

## Ids
From the mod id and the block's name: the template, variant (`<mod>:<name>`), arsenal, attacks (`<mod>:attack:<name>`), sequences, phases and states
(`<mod>:state:<name>`, the same id the cook gives the graph's state). The cooked graph is the animation block's name, or `<animation>.<body>` for a body
with slots. `Was` keeps the ids through a rename.

## Limits and costs
| What | Today | What it takes |
|---|---|---|
| A body or animation with files written in C# | Data files only | A source generator that hands the build the block's cook steps: M |
| Dodges and the base's other motions on a body of your own | Off (taunt, blocking, call for help, idle turns, sleep, spawn sequence) | Copies onto your states, as hit reactions are: S per kind |
| `Sight` with line of sight in a brain's own perception | Distance and faction only | A raycast per candidate: S |
| A new FSM state, faction relations, villager routines | Not built | Hooks on `HandleCondition`/`StateDispatch`, the faction leaf, `DecideTask`: M each |
| Ragdolls | A `Dead` state | Client physics: XL, not planned |

## Test
`mods/tests/blocks-npc-test`: run `build-assets.py` (it writes the creature's glTF and cooks it), then start a lab twice with the force-ready switch
(`kiln/data/kiln.creatures/force-ready`). It logs `blocks-npc-test: PASS` and `FAIL` lines, and `blocks-npc-test done: N PASS, M FAIL`.
