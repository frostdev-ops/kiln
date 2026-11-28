# Combat And Attack Tweaks on Kiln's action speed

For the Combat And Attack Tweaks author: which of the 20 sites in the beta.6 native notes each Kiln API replaces, and
what is still yours. Kiln 0.2.0-dev.31.30 (mod API 0.2.28). Speed: `Kiln.ActionSpeed`
([action-speed.md](action-speed.md), [../services/combat.md](../services/combat.md#action-speed)). Input:
`Kiln.Client.GameInput`, documented with Kiln.Client. Evidence for every site on both exes:
[../../re/action-speed.md](../../re/action-speed.md).

## The port in short
```csharp
// in OnLoad, on the host or the dedicated server (Client.IsRemote is false there)
if (ActionSpeed.Available && !Client.IsRemote)
{
    if (cfg.MeleeSpeed.Enabled) ActionSpeed.SetDefault(ActionKind.Melee, cfg.MeleeSpeed.Multiplier);
    if (cfg.BowSpeed.Enabled) ActionSpeed.SetDefault(ActionKind.Bow, cfg.BowSpeed.Multiplier);
    if (cfg.ToolSpeed.Enabled) ActionSpeed.SetDefault(ActionKind.Tool, cfg.ToolSpeed.Multiplier);
}
// "Apply" in your config menu: SetDefault again (or ClearDefault for a switched-off category)
```
- The three speed fields no longer need `[Synced]`. Kiln sends the effective rates to every Kiln client itself
  (channel `kiln:speed`), and a guest applies them to its own player's prediction and presentation. Keep `[Synced]` only
  if your menu should show the host's values on a guest; `ActionSpeed.Get(player, kind)` reads them there too.
- Your range (0.5 to 10) is Kiln's: `ActionSpeed.Min` and `ActionSpeed.Max`. Values from several mods multiply.
- Remove: `NativeCode.cs` and `AttackSpeedHooks.cs`'s byte patterns, the return-address filters, the prediction wraps
  around `actor_simulation_start`, `server_update_animation_graph_2` and `server_player_ranged_weapon`, the route table
  (`AttackSequences.cs`), the bow timing hook, and `Multiplayer.cs`'s speed sync.
- Manifest: `"kiln": ">=0.2.0-dev.31.30"`, built against MDK 0.2.0-dev.31.30.

## Site by site
| # | Your site | Kiln now | What you do |
|---|---|---|---|
| 01 | ClientSequenceClockCode | `speed/SeqClock` (client): Kiln scales the elapsed time of a classified action | `ActionSpeed.SetDefault` / `Set` |
| 02 | DedicatedSequenceClockCode | `speed/SeqClock` (server exe, rsi contract) | the same call |
| 03 | ServerSequenceCallCode | a resolve-time anchor in sigdb; Kiln tells authority from prediction with its own kiln.world wraps | nothing |
| 04 | ClientSequenceCallCode | the same, for actor_client_play (presentation, the local player only) | nothing |
| 05 | CurrentEntityCode | Kiln's own `world/CurrentEntityId` | nothing |
| 06 | BindGraphCode | `speed/GraphBind`: binds the player's graph instance to the rate of the action the clock site saw | nothing |
| 07 | ServerGraphCallCode | anchor; the actor's frame slot `[rbp-0x7C]` is a fixed sigdb constant | nothing |
| 08 | ClientGraphCallCode | anchor; `[rbp+0x14]` | nothing |
| 09 | EvaluateGraphCode | `speed/GraphEval`: scales xmm3 on the first pass for a bound instance | nothing |
| 10 | BowTimingCode | `speed/BowTiming`: divides charge and fire durations when weaponType is Bow (0) | `ActionKind.Bow` |
| 11 | HandSlotsCode | resolved in sigdb (`speed/HandSlots`), **not hooked in this release** | keep yours for now (below) |
| 12 | SlotToWorldCode | resolved (`speed/SlotToWorld`), not used yet | keep yours |
| 13 | ColliderCreateCode | resolved (`speed/ColliderCreate`), not hooked | keep yours |
| 14 | ColliderMoveCode | resolved (`speed/ColliderMove`), not hooked | keep yours |
| 15 | CastShapeCode | resolved (`speed/CastShape`), not used yet | keep yours |
| 16 | CollisionBufferCode | resolved (`speed/ContactWriter`), not hooked | keep yours |
| 17 | ImpactContactCode | resolved (`speed/ImpactContacts`), not hooked. The stock instruction there tests **bit 1** (0x02) of `+0x1F3`; check your bit-0 condition against it | keep yours |
| 18 | CollisionFlagCode | resolved as the getter (`speed/CollisionBackend`). Your +7 fix is right: 0x283F334 / 0x14DB314. Kiln will call the getter | keep yours (with the +7 fix) |
| 19 | InputWriterCode | Kiln.Client `GameInput` (OnSample, Repeat, Press/Release, BowFullyDrawn) | port Hold to Light Attack to GameInput |
| 20 | InputMessageCode | Kiln clears stale synthetic presses itself (GameInput) | nothing |

## What is different from beta.6
- **Classification.** Kiln does not use SequenceRuntimeIds, graph states or overlays. It reads the running action's
  trigger and its used item's equipment slot type from the game's reflection and item data. Melee weapon with
  Primary, Primary_Tap, Primary_Hold or Heavy is Melee. Tool is Tool. Bow is a ranged weapon's Primary trigger while
  the player's ranged weapon type is Bow. If one of your routes is missing (an attack Kiln leaves at the game's timing),
  tell us which action, and we add the trigger or slot to Kiln's table.
- **Graph.** While the classified action runs, Kiln scales the player's whole graph instance, not just the matched state
  or overlay. Legs move at the rate while a player attacks on the move.
- **Melee sampling above 2x** is not in this release (`ActionSpeed.Status.MeleeSampling` is false; capability
  `combat.melee_sampling` says `service-older`, "planned: needs an in-game check first"). Kiln's sampler follows in a
  later release, after an in-game check. Until then you may keep your own contact sampling (sites 11-18) next to Kiln's
  speed, and read the effective melee rate with `ActionSpeed.Get(player, ActionKind.Melee)` instead of your config.
  Kiln's clock site does not hand out the step's sequence interval: if your sampler took it from your own site 01/02
  hook, keep that hook as a read-only observer, so nothing is scaled twice. When Kiln ships its sampler,
  `Status.MeleeSampling` turns true and you drop sites 11-18.
- **Untested above about 3-5x on busy dedicated servers.** Your lab numbers (beta.6, bots) are in
  [../services/combat.md](../services/combat.md#melee-contact-sampling).
- **Who applies what.** The host or dedicated server has authority. A guest applies the server's rates only to its own
  player. Guests on an older Kiln see server-timed results.

## What is left for Kiln's sampler
The hand-pose history per actor at site 11, through SlotToWorld (12). Interpolated box poses (lerp position and scale,
slerp rotation, up to ten) cast with CastShape (15) at sites 13 and 14, into a bounded thread-local list redirected at
site 16. A merge into the impact's contact stream at 17 within its capacity, with your dedupe rules (pending, native and
reversed pairs). The backend check through 18's getter. The BoxColliderData checks (0x60 bytes, shape 0x88154C34,
mask 0x200, enabled +0x4B, continuous +0x5C), and the SpawnImpact window intersection. Kiln's sites resolve on both
exes today. The game-free part (window intersection, sample times, pose interpolation, merge and dedupe, fixed
storage) is in `native/services/combat/sampler_core.h` with host tests, including swings shorter than one and two
server ticks and colliders that appear late. Binding the sites, a lab run and an in-game check are the remaining work.
