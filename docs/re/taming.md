<!-- SPDX-License-Identifier: MIT -->
# The game's own taming (wildlife, bait, petting, the farm transformation)

Server build `f0d824be…` (Steam 26430253), client `5d9ef2f1…` (Steam 26430625): the builds of dossier chapter 19e,
which this page continues (19e sections 1, 2 and 4.6). Static reading with capstone over the PE image (the exe is only
read), the game data through `tools/kfc` over the server's own data, and a player-free lab probe
(`mods/tests/native-taming-test`). Evidence: `/mnt/Dongus/kiln-wave/dev32/out/tame/re/` (disassembly, decodes,
signature checks) and `/mnt/Dongus/kiln-wave/dev32/out/tame/lab/` (kiln.log of each run).

Tags: **[static]** read from the code, **[data]** decoded from the game's resources, **[lab]** seen in a running
server, **[open]** not settled (what a signed-in player must still show: lab steps for the LABBOT bot below).

## 1. In one paragraph

A tameable wild animal has a favourite **bait**: a placed entity (`FoodBait_*` template) that its bait item
(`Food_*_Bait`, category AnimalFood) places with the building cursor. The animal senses a bait of exactly that template
within its sensor range, walks to it and eats it. **Eating alone tames nothing**: a step counts only when the player
**pets** the animal while it eats (the interaction the game offers then): the animal's synced pet sequence fires a
`TameWildlifeEvent`, the bait is consumed and the animal's progress goes up by one. At its required number of steps the
farm transformer makes it a farm animal (faction FarmAnimal, `IsTame` added). A startle (a hit, a loud noise) costs
progress by the world setting `tamingStartleRepercussion`. Progress belongs to the animal, not to a player: the game
keeps no record of who tamed it.

## 2. Who can be tamed, and by what [data]

A template is tameable when its `Wildlife.enabledFeatures` has `Tameable` (bit 3); the farm transformer also asks for
`Animal`. **`Wildlife` lives in the template only** [lab]: a spawned wild dog has no per-entity `Wildlife` (nor `Animal`)
component, and its taming setup is read through the template by reflection path (kiln.creatures' `Ai.Get`, section 6).
Its `Wildlife.tamingSetup` (`keen::wildlife::TamingSetup`, 72 bytes at `Wildlife+0x1c0` of the template data) names the
bait and the steps:

| Field | Offset in TamingSetup | Meaning |
|---|---|---|
| `isWildlifeBehaviorActiveWhileTamed` | +0x00 | the wildlife program keeps running after the tame |
| `favoriteFood` | +0x04 (16-byte TemplateReference) | the bait entity's template (`FoodBait_*`) |
| `necessaryTamingSteps` | +0x14 (u8) | the steps to the farm transformation |
| `foodBaitSensorRange` | +0x18 (f32, m) | how far it senses a bait |
| `foodBaitAngleCheckMinDistance` | +0x1c | |
| `foodBaitEatMaxDuration` | +0x20 (Time, ns) | how long it eats one bait |
| `alertDurationHigh`, `alertDurationMediumFoodEat` | +0x28, +0x30 | alert times (startle) |
| `foodBaitEatAdaptFleeRadiusDistance`, `tamingApproachFinishedFleeRadius`, `foodBaitEatSnoutPivotDistance` | +0x38, +0x3c, +0x40 | placement while eating |

The animal's pet interaction is `Wildlife.tamingPetInteractionSequence` (+0x2c0) with its verb `+0x2d0`.

The tameable animals of this build and their baits (wild templates; the `PLACEMENT_HELPER_*` and `Base_*` templates are
copies or bases):

| Animals (wild templates) | Bait template | Bait item | Steps | Sensor |
|---|---|---|---|---|
| `Animal_Wildlife_Dog_0[1-5]_*_AG2` | `FoodBait_T2_Dog` `21eb90be…` | `Food_T2_Dog_Bait` (0xb59dfc2c) | 2 to 4 by variant | 8.5 m |
| `Animal_Wildlife_Cat_0[1-3]_*_AG2` | `FoodBait_T2_Cat` `49369904…` | `Food_T2_Cat_Bait` (0x2ee26768) | 2 to 4 | 8.5 m |
| `Animal_Wildlife_T1_Goat_AG2`, `…_T5_Goat_Coldheights_Variation_Mountaingoat` | `FoodBait_T1_Goat` `0ea25887…` | `Food_T1_Goat_Bait` (0x5c236b2c) | 1, 3 | 10 m |
| `Animal_Wildlife_T2_Bunny_AG2`, `…_Bunny_Coldheights_Variation` | `FoodBait_T1_Bunny` `153e3869…` | `Food_T1_Bunny_Bait` (0x37821c6d) | 1 | 8 m |
| `Animal_Wildlife_T2_Catdeer_AG2` | `FoodBait_T2_Catdeer` `18c5f6de…` | `Food_T2_Catdeer_Bait` (0xc8f10f17) | 2 | 6 m |
| `Animal_Wildlife_T3_Bird_Turkey_AG2`, `…_T4_Bird_Desert_AG2` | `FoodBait_T3_Turkey` `d66b6a08…` | `Food_T3_Turkey_Bait` (0xa4a95e84) | 2 | 7 m |
| `Animal_Wildlife_T5_Yak_AG2` | `FoodBait_T5_Yak` `a24ac87c…` | `Food_T5_Yak_Bait` (0x860c9dc0) | 3 | 9 m |
| `Animal_Wildlife_T6_Capybara_AG2` | `FoodBait_T6_Capybara` `bef8e34e…` | `Food_T6_Capybara_Bait` (0x50f09d3f) | 2 | 10 m |
| `Enemy_Animal_Wildlife_T4_Transformer_Sanddigger` | `FoodBait_T4_Sanddigger` `44bde8af…` | `Food_T4_SandDigger_Bait` (0x80703fa6) | 3 | 8.5 m |

The list is from the dossier's offline census (`dev28-rescue/out/pets/evidence/template-census.json`), checked against
the current data for the dog (`Animal_Wildlife_Dog_05_white_AG2`: Tameable, `FoodBait_T2_Dog`, 2 steps, 8.5 m, 16 s:
`out/tame/re/tpl-dog05.json`). Fish (`*_tame` fish templates) carry `IsTame` from the start and no bait.

**The bait item** (`Food_T2_Dog_Bait`, `out/tame/re/item-dog-bait.json`): category `AnimalFood`, stack 50, an
equipment entry whose primary action places `equipment.placedEntity` = the bait template within 8 m
(`checkRequiredMaterialsOnPrimaryAction`: the placement takes one item). **The bait entity** (`FoodBait_T2_Dog`,
`out/tame/re/food-dog.json`): `FoodBait` (a 1-byte marker), `PickupItem` and `DestroyOnLoot` (a player can pick it up
again), `DoNotSave` (a bait is never saved), a collider, gravity.

## 3. The state machine [static]

Programs (all on the server; the client has its own copies at the RVAs in 19e):

| Program | RVA | Role |
|---|---|---|
| `wildlife` | `0x21f880` | per entity: `0x21e8b0` (behavior, a jump table over `DynamicWildlife.state` at `0x21f17d`), then `0x21df70` (movement) |
| `wildlife_farm_animal_transformer` | `0x21fe70` | `Tameable` and `necessaryTamingSteps <= tamingProgress` and no IsTame: faction 12 (FarmAnimal), queue `IsTame` (`0x1399e0ba`), DismantleOverride |
| `interaction_taming` | `0xa8320` | no IsTame, state 15, a bait, the actor not dead: the offer shows the template's pet sequence and verb (`InteractionOfferRuntime.overrideSequence +0xc`, `overrideVerbLocaId +0x14`) |
| `interaction_animal_tame_default` | `0xa45f0` | with IsTame: the farm animal's own default interaction |
| `wildlife_enemy_transformer` | `0x21fa80` | the hit / no-hit switch to enemy behavior (transformer animals), not taming |

`keen::ecs::DynamicWildlife` (176 bytes, the server's own; not replicated): `state` +0 (`wildlife::State`: Dead 12,
FoodBaitPursue 14, FoodBaitEat 15), `lastState` +1, the eat timer `+0x60`, the startle time `+0x70`, the time of the last
counted step or finished bait `+0x78`, `foodBaitEntityId` +0x88, `hasSensedFoodBait` +0x8c, `hasFullyAtenFoodBait`
+0x8d, `tamingProgress` +0x8e.

- **Sensing** (`0x220253`…): nothing while `tamingProgress >= necessaryTamingSteps` (`0x220269`); a bait in range,
  sensed and not already pursued (last state not 14/15) starts the pursuit (state 14).
- **Eating, no petting** (`0x2218a0`…): while the bait exists and the eat timer has not run out, the animal eats
  (state 15). When the timer runs out: `+0x78` = now, the bait entity is destroyed (`0x5fbe20`), `hasSensedFoodBait` 0,
  `hasFullyAtenFoodBait` 1, the bait id and the timer 0, state 0 (`0x21e540(…, 0)`). **No progress.** A bait that
  disappeared first (picked up, destroyed): `hasFullyAtenFoodBait` 0, state 9 (`0x221974`).
- **The step** (`0x21c4f5`…`0x21c5b7`): the actor's sequence events of this step are collected (`0x34520`); each one
  of type `keen::actor::TameWildlifeEvent` (reflection hash `0x537b675c`) while state is 15 and a bait is set: the bait is
  destroyed, `hasSensedFoodBait` 0, `hasFullyAtenFoodBait` 1, the bait id and the timer 0, then
  `tamingProgress = min(tamingProgress + 1, necessaryTamingSteps)` (`0x21c58e`…`0x21c5a7`: the increment happens even when
  the bait was already gone), `+0x78` = now.
- **The startle** (`0x21d19e`…`0x21d1f1`): the world's `tamingStartleRepercussion` (getter `0x945ee0`:
  `GameSettings+0x75` through the BalancingRegistry) decides: KeepProgress 0 nothing, LoseSomeProgress 1 one step back
  (never below 0), LoseAllProgress 2 back to 0; `+0x70` = now. Kiln.Settings already sets this world setting
  (`tamingStartleRepercussion`, default LoseSomeProgress, `docs/re/settings.md`).

**Where the TameWildlifeEvent comes from** [data]: the player's interaction sequence `Interaction_Pet_Wildlife_Taming`
(`2e70d813…`, the dog's `tamingPetInteractionSequence`) has a `PetAnimalEvent` and, at 0.3 s, a `SyncedSequenceEvent`
that makes the animal play `Enemy_Wildlife_Pet_Taming` (`09ad9f39…`), whose only events are a state change and the
**`TameWildlifeEvent` at 3.75 s** (`out/tame/re/seq-dog-pet.json`, `seq-dog-synced.json`). So one step = one petting
that lasts 3.75 s while the animal eats the right bait; a petting while it does not eat counts nothing.

## 4. Who tamed it, saves, bases, replication

- **No owner** [static]: neither DynamicWildlife nor the farm transformer nor AnimalSaved (56 bytes: spawn position,
  food and reproduction times, needs) names a player. The step reads only the animal's own sequence events. Who petted
  can only be read where the player's interaction starts the synced sequence: **[open]**, the attribution site is not
  derived (a player is needed to observe it; `taming.native_observe` stays off).
- **Saves**: `DynamicWildlife` is the server program's working state; whether a half-tamed animal keeps its progress over
  a save is **[open]** (a lab with a restart; nothing in the static reading writes it to `AnimalSaved`). A bait is
  `DoNotSave`. A tamed animal keeps `IsTame` and faction 12 by its template's saved components **[open: a restart]**.
- **Bases**: `RegisterAnimalInBase` (`0x4ecc0`, 19e) registers live tamed animals with `PlayerBasesSystemServer` by
  position; there is no player in it.
- **Replication**: `NetworkWildlife` (24 bytes: alert +0, alert time +8, `tamingProgress` +0x10, `isEatingFoodBait`
  +0x11) is written by the wildlife program (`write_opt`); what a client draws from it is **[open]** (a client).

## 5. What Kiln hooks, reads and offers (dev.32)

Nothing in the game's taming is hooked. Kiln (Kiln.Taming's `NativeTaming`) reads the Wildlife template's taming setup by
its reflection paths (`Wildlife.enabledFeatures`, `Wildlife.tamingSetup.*`, `Wildlife.tamingPetInteractionSequence`,
through kiln.creatures, once per template), the per-entity `DynamicWildlife` by its bytes (its size is checked against
this build's reflection), and `IsTame`. It plays the animal's own pet sequence for a server-side step
(`Abilities.PlaySequence`, not aborting what runs), places a bait template, and leaves every native offer alone. Signatures: 19e's four programs
resolve exactly once at the same RVAs on both current exes (`out/tame/re/sigcheck-{server,client}-taming.log`): nothing
moved since dev.28.

## 6. Lab [lab]

`mods/tests/native-taming-test` on dedicated lab servers, no game client: four runs with no player and one with a LABBOT
bot player (`tools/labbot`, dev32/labbot 8e1d701d) standing 4 m from the animals, on 2026-11-27, server build
`f0d824be…`, Kiln at dev32/tame. Logs: `out/tame/lab/kiln-native-1127-{0737,0742,0752,0756}.log` and
`kiln-bot-1127-0855.log` (the whole kiln.log), `lines-*.txt` (the probe's lines). The wildlife states by name (reflection,
`wildlife::State`): 1 Start, 2 Spawning, 3 Idle, 4 Alert, 5 Flee, 6 Roaming, 7 RunAway, 8 Hit, 12 Dead, 14
FoodBaitPursue, 15 FoodBaitEat.

| Run | Result | What it showed |
|---|---|---|
| 0737 | 0 PASS, 2 FAIL | the probe read `Wildlife` as an entity component and threw: there is none |
| 0742 | 5 PASS, 4 FAIL | the components a spawned wild dog has, and the template's setup by reflection path |
| 0752 | 6 PASS, 4 FAIL | `NativeTaming.Read` (now over the template) right in a game; no simulation with no player near |
| 0756 | 8 PASS, 4 FAIL | the farm transformation and Kiln's native events with no player near |
| bot 0855 | 17 PASS, 4 FAIL | with a bot near, the dog is simulated; Kiln's own steps end to end with the bot as the tamer (section 6.1) |
| bot2 1027 | 17 PASS, 4 FAIL | the same on the reviewed LABBOT (dev32/review-labbot ee3b67e4); the dog went RunAway (7) and Roaming (6) near the bot and still sensed no spawned bait |

What the lab settles:

- **Components of a spawned `Animal_Wildlife_Dog_05_white_AG2`** (0742): `DynamicWildlife` 176 bytes, `NetworkWildlife`
  24, `InteractionOfferRuntime` 56, `AnimalSaved` 56; **no `Wildlife`, no `Animal`, no `IsTame`**. Wildlife is template
  data only, which is why the dossier's offsets are template offsets.
- **The template's taming setup by reflection path** (kiln.creatures `Ai.Get`, read from the shared template):
  `Wildlife.enabledFeatures` = `Roaming|Flee|Tameable|RunAway`, `tamingSetup.favoriteFood` =
  `21eb90be-0ff2-477d-a804-dee22e9a2623` (FoodBait_T2_Dog), `necessaryTamingSteps` = 2, `foodBaitSensorRange` = 8.5,
  `foodBaitEatMaxDuration` = 16 (seconds), `isWildlifeBehaviorActiveWhileTamed` = false,
  `tamingPetInteractionSequence` = `2e70d813…` (Interaction_Pet_Wildlife_Taming). Every value matches the static reading
  (section 2). The verb's field is not `tamingPetInteractionVerb` (refused): its reflected name is still **[open]**.
- **`NativeTaming.Read` in a game** (0752, 0756): Tameable, 2 steps, the bait template, 8.5 m, 16 s, phase Wild, state
  1. The first runs failed on the same mistake the dossier's layout invited (`World.ReadBytes(dog, "Wildlife")` is null);
  Kiln.Taming reads the template since this lab.
- **No player near: the wildlife program does not move the animal** (0752, 0756). Over 6 s the dog's position and every
  byte of its `DynamicWildlife` stayed the same (`state 1` Start, `lastState 1`, the rest 0: the dossier's probe dumped
  the same bytes in dev.28). **With the bot 4 m away it runs** (bot 0855): Spawning then Idle, its position and
  `DynamicWildlife` changed within 6 s. Its own bait 2.5 m away was never sensed (state never 14 or 15), its `InteractionOfferRuntime` stayed
  zero, and the pet sequence played on it did not run (`SequenceOf` 0). So the bait, the eating, the petting, the
  step and the startle (section 3) **cannot be shown without a player**: the four FAIL lines of the first four runs are
  exactly these. **With the bot near they still fail** (bot 0855): the Idle dog never sensed its own bait 2.5 m away in
  8 s (`hasSensedFoodBait` 0, no state 14), so no eating, step or tame followed. The bait was spawned by Kiln from its
  template; see the next point. **[open]**: the natural flow needs a bait the game itself places (section 7).
- **The farm transformation runs with no player near** (0756): a fresh dog's `tamingProgress` written to 2 (a raw write
  of the server's working state, lab only) made it a farm animal within 40 steps: `IsTame` present, faction FarmAnimal,
  `NativeTaming.Read` phase Tamed 2/2.
- **Kiln's watch** reported `taming 0 -> 2 of 2 (Step)` and `tamed after 2 step(s)` for it (`NativeTamingProgressEvent`,
  `NativeTamedEvent`). The watch sees only the progress, so it reports a raw write as a step.
- **`NativeTaming.PetStep`** refuses a tamed dog (`Tamed`) and a wild dog that does not eat (`NotEating`); that it counts a
  step on an eating dog is **[open]** with the eating itself.
- **A spawned bait** (`FoodBait_T2_Cat`, `FoodBait_T2_Dog`) has `PickupItem` and `DoNotSave`, but
  `World.Has(bait, "FoodBait")` is false, and the simulated dog did not sense it (above). The likely reading: the bait
  item's placement makes what the sensor looks for, a template spawn does not. So `NativeTaming.PlaceBait` (a template
  spawn) and the counted half of `NativeTaming.PetStep` ship **experimental (KILNX002)**; which part of the placement
  the sensor needs is **[open]**.
- **IsTame is readable per entity** (0756, bot 0855: `World.Has(dog, "IsTame")` true after the transformation), unlike
  `FoodBait` on a spawned bait.
- **Rows**: `taming.native` (sigdb) Available, `kiln.taming` and `taming.steps` Available, `taming.native_observe`
  Unavailable (policy, as designed). The startle setting reads `LoseSomeProgress` (the default).

### 6.1 Kiln's own steps with a bot as the tamer (bot 0855)

The probe's step G ran Kiln.Taming end to end with the bot as the player:
- Begin on a game wolf (`Enemy_Wildbeast_Wolf_earlygame`, made Wildlife so it does not attack the bot);
- a weaken step (health at most 100 %, no cost);
- `Taming.Choices` (the bot's slot of 3 raw lean meat);
- a feed of 2 from that slot through `kiln.item_costs`: 3 to 1, receipt committed, native ticket 1.

The tame committed and bound a Kiln.Pets record of `native-taming-test:wolf-pet` to the bot's character (Ready; its
summon was off: the pets review is fixing summon), and the wild wolf was removed. An escrow attempt fed one meat
(1 to 0), and its cancel refunded it (1, refund receipt committed). Events: StepChanged 1/2 and 2/2, Tamed, then
StepChanged 1/2 and Canceled with 1 refund.

## 7. Steps that need a signed-in player (for the LABBOT bot)

Each is a step a bot player can drive on a dedicated lab server; none needs a game client.

0. **Done** (bot 0855): the wildlife program runs with a player near. **Next**: a bait the game places. The bot
   cannot use an item yet (LABBOT has no "use item"), so either LABBOT gains the bait item's primary action, or Kiln
   finds which part of the item's placement the bait sensor needs (the bait has no `FoodBait` per entity when spawned).
   Then `mods/tests/native-taming-test`'s steps A to E (its own bait sensed and eaten, eaten to the end without a step,
   the pet sequence while it eats as a step, the farm transformation at 2 steps, a startle).

1. **Bait placement**: equip `Food_T2_Dog_Bait`, place it within 8 m of a wild dog; expect one item fewer and a
   `FoodBait_T2_Dog` entity at the cursor, the dog's state 14 then 15.
2. **The pet offer**: while the dog eats, the interaction offered to the bot is the pet sequence (`overrideSequence` =
   FNV of `2e70d813…`) with the taming verb; before and after eating the game's default.
3. **Petting**: accept it; expect `tamingProgress` + 1 at about 3.75 s after the acceptance and the bait consumed;
   record which entity started the synced sequence (the attribution site, to turn `taming.native_observe` on).
4. **Two bots**: two players pet the same eating animal: one step or two.
5. **Finalisation on reconnect**: tame to one step short, leave, rejoin; whether the progress stays (and over a server
   restart); then finish and check `IsTame`, faction 12, base registration in a base, and what a rejoining player sees.

## 8. The taming fields of the creatures registry (dev.32 review)

The tameable data is on the creatures registry (schema 0.1.1), mapped onto the template's `Wildlife` (the only copy: section
6, no spawned animal has a `Wildlife` component, so every reader reads the template):

| Field | Mapping | Read | Writer |
|---|---|---|---|
| `creatures.tameable` | the `Tameable` bit of `Wildlife.enabledFeatures` (conv `flag`, new: one named bit of a bitmask) | now: the farm transformer checks it at every update (section 3) | none: read only (Kiln has no writer of one bit yet) |
| `creatures.tamingSteps` | `Wildlife.tamingSetup.necessaryTamingSteps` (u8; the schema range is 1 to 255) | now: the wildlife program (sensing stops at it, the step is capped at it) and the farm transformer (tames at it), every update | live, kiln.gamedata's templates section, as `sight.range` |
| `creatures.favoriteBait` | `Wildlife.tamingSetup.favoriteFood` (a template reference: a `templates` id, `game:foodbait_t2_dog`) | now: the bait sensor at every update | none: read only |

`regmap_check.py --exe` on the server exe (read as data): all 171 rows resolve and type-check. **Not proven in a lab yet**: the
server record was proven on the regmap before these rows, so this branch's edit of `regmap/common` leaves every registry row of
the build unproven until the dev.32 integration re-proves the merged mapping (`kilnctl regproof`, then the client mirror);
the three rows then get their own lab evidence (the producer's live round trip of `tamingSteps`, kiln.content's read-only
samples of `tameable` and `favoriteBait`). What a write of `tamingSteps` does to an animal already partly tamed is the static
reading above (a progress that reaches the new number tames it at its next update), not a lab result.
