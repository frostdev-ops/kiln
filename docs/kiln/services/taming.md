<!-- SPDX-License-Identifier: MIT -->
# Kiln.Taming: the taming framework (since 0.2.0-dev.32)

Kiln makes taming possible; it does not decide what taming is. Kiln.Taming ships the mechanism of dossier chapter 19e:
attempts, server challenges and a replay cache, exact-cost steps with receipts, timeouts, before- and after-events, and
the settlement of costs at the end. It also ships the game's own taming as mechanism (`NativeTaming`). **Every policy is
the mod's declaration** (`TamingDefinition`):
- what each step costs;
- whether paid steps come back when an attempt fails (non-refundable, refund on fail, escrow);
- the timeouts and the range;
- which creatures it tames;
- the pet a tame makes.

Kiln's sample is only a sample.

| | |
|---|---|
| Side | server (the authoritative world) |
| Threads | every `Taming.*` and `NativeTaming.*` call on the tick thread |
| Capabilities | `kiln.taming` (definitions: a mod defines its tamings when it loads, before any world runs), `taming.steps` (Kiln's steps: the attempts, while a server world runs), `taming.native` (the game's taming read and driven: sigdb), `taming.native_observe` (who tamed a native animal: **off**, not proven); an attempt of a definition also needs the rows of its steps: `items.costs.exact` (feed), `items.costs.wear` (tool), `taming.native` (native), `pets.records` (a definition that names a pet) |
| Costs | [item-costs.md](item-costs.md): `ItemCosts.Take` for feed steps, `ItemCosts.Wear` for tool steps, `ItemCosts.Refund` for refunds |
| Binds to | [pets.md](pets.md): a tame of a definition with `Pet` grants a Kiln.Pets record |
| Game research | [docs/re/taming.md](../../re/taming.md): the game's taming (wildlife, bait, petting, the farm transformation) |

## A taming, as a mod declares it

```csharp
Taming.Define(new TamingDefinition
{
    Id = "my-mod:wolf",
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],          // creatures registry ids; "game:animal_wildlife_dog_*" for a prefix
    Steps = [new WeakenStep(0.3), new FeedStep(meat, 2), new UseToolStep(whistle, Wear: 5)],
    Pet = "my-mod:wolf-pet",                             // a Kiln.Pets definition: a tame grants a record of it
    Refund = TamingRefundPolicy.Escrow,                  // the default is NonRefundable
});
```

The steps run in order; the last one tames.

| Step | What it checks | What it costs |
|---|---|---|
| `FeedStep(item, count)` | the selection is the owner's plain stack of the item holding `count` | `count` items from that slot only (`ItemCosts.Take`) |
| `WeakenStep(maxHealthFraction)` | the target's health is at most the fraction of its maximum (no damage is dealt by the step) | nothing |
| `UseToolStep(item, wear, allowBreak)` | the selection is the owner's stamped gear item of that item | `wear` durability through the game's debit (`ItemCosts.Wear`; 0: a use that costs nothing) |
| `NativeStep(steps)` | the game's own progress of a tameable wild animal rose by `steps` since the previous applied step (a player petting it while it eats its bait, or `NativeTaming.PetStep`) | nothing of Kiln's (the bait is the game's) |

Fields and bounds:

| Field | Default | Bounds |
|---|---|---|
| `Steps` | - | 1 to 64. Feed 1 to 999 items; weaken in (0, 1]; tool wear 0 to 32767; native 1 to 255 |
| `Creatures` | - | at least one id |
| `RangeMeters` | 3 | 1 to 8 |
| `RequireThresholdUntilCommit` | true | the latest weaken threshold holds for every later step and the tame |
| `InactivitySeconds` | 30 | 1 to 300 |
| `MaxAttemptSeconds` | 300 | at least the inactivity, at most 3600 |
| `Refund` | `NonRefundable` | `NonRefundable`, `RefundOnFail`, `Escrow` |
| `Pet` | null | a Kiln.Pets definition id |
| `RemoveTarget` | true | with a pet |
| `SummonPet` | true | with a pet |

The same in JSON: `Taming.DefineJson(json)`, one object or an array ([modding/taming.md](../modding/taming.md)).

## An attempt

| Call | What it does |
|---|---|
| `await Taming.Begin(player, target, definition, request)` | A task, as every action's answer (it takes no cost, so it is complete at once). Checks the definition, the owner, the target (its creature, alive, in range) and the claim. It posts `TamingBeginningEvent` (cancelable), claims the target (another attempt on it is `Busy`) and holds the pet's roster slot. It then posts `TamingStartedEvent` and answers with the first token. |
| `Taming.Advance(player, attempt, request, selection)` | Checks the token, range, target and threshold, and posts `TamingAdvancingEvent`. For the last step it also preflights the pet's slot and posts `TamingCommittingEvent`. It takes the exact cost and applies the step (`TamingStepChangedEvent`). After the last step it tames. |
| `Taming.Retry(player, attempt, request)` | The cost-free retry of a deferred tame (`AwaitingCommit`): it checks again and binds, taking nothing. |
| `Taming.Cancel(player, attempt, request)` | Ends the attempt; a cost in flight settles first. |
| `Taming.Inspect(attempt)` / `Taming.Choices(player, attempt)` | Reads only: the attempt now, and the owner's slots that can pay the next step (as server selections: the first 256, with `Truncated` when more could pay). |

**Requests and tokens.** Every action takes a `TamingRequest(RequestId, Token)`, the one request shape of Kiln.Pets
(`PetRequest(RequestId, ExpectedStateRevision)`) and Kiln.Taming: `TamingRequest.New(progress.Token)`. Each answer's
`Progress.Token` (and `Choices(...).Token`) is the token for the next action: `StepChallenge` (random, rotated by every
applied step), `ExpectedRevision` and `StepIndex`. A client echoes it with its own request id (`Begin` takes none):
- **A new id for each intended action**, and **the same id for a network retry**: the same request again returns the
  same task or result and never a second cost.
- **The same id with another action** is `RequestConflict`.
- **An old token under a new id** is `StaleStep`, so a completed tool step can never be replayed against an identical
  next one.

**Refusals.** A refused call answers `Ok = false` with a reason for a log, and its first word as a value,
`TamingResult.Refusal` (`TamingRefusal`: `NotOwner`, `OwnerOffline`, `Busy`, `StaleStep`, `RequestConflict`,
`OutOfRange`, `Threshold`, `TemplateMismatch`, `Capacity`, `RefusedByMod`, `CommitDeferred`, `AttemptClosed`, ...;
`Other` for a word a later Kiln adds). Decide by it, never by the text: `if (r.Refusal == TamingRefusal.TemplateMismatch)
...`. The same meaning has the same word as Kiln.Pets' `PetRefusal` (`Busy`, `Capacity`, `NotOwner`, `OwnerOffline`,
`RequestConflict`, `RefusedByMod`, `UncertainCommit`, ...). A mod's veto of a before-event is `RefusedByMod` (the attempt
goes on); `Canceled` is only an attempt that ended canceled.

Limits (Kiln's policy, not the game's):
- 10 requests a second per player;
- 256 requests per attempt;
- 512 running attempts per world;
- closed attempts answer their replays for 10 minutes of simulation, then stay closed (`AttemptClosed`), never run again.

**States.**

| State | Meaning |
|---|---|
| `Active` | waiting for the next step |
| `CostPending` | a cost or the bind settles (another request is `Busy`) |
| `AwaitingCommit` | every step is paid; the tame waits for `Retry` |
| `Committed` | tamed |
| `Canceled` | canceled |
| `Failed` | a failure, in a word: `BodyGone`, `OwnerLeft`, `WorldEnded`, `DefinitionGone`, `UncertainCommit`, `PartialCostCommitted`, `Refused: ...` |
| `Expired` | `InactivitySeconds`, `MaxAttemptSeconds` |

**Timeouts** run on the game's own 60 Hz world clock (each step adds what the clock advanced in it, as Kiln.Pets' timers:
a dedicated server steps 15 to 25 times a second, so a step is not 1/60 s; a paused world's clock stands still).
- Only an applied step refreshes the inactivity timeout. Refusals, replays, `Inspect`, `Choices` and failed retries do not.
- The earlier deadline wins, in `AwaitingCommit` too.
- A timeout, cancel or failure that comes while a cost or the bind settles waits for it: the receipt decides what was
  spent, and a cost is never assumed away.

**Order of events.**
- Success: `TamingAdvancingEvent`, `TamingCommittingEvent`, then `TamingStepChangedEvent`, `TamedEvent`,
  `TamingCompletedEvent`. Kiln.Pets' `PetGrantedEvent` comes in the step the record is made, before `TamedEvent`.
- Deferred: `TamingStepChangedEvent`, then `TamingCommitDeferredEvent`.
- The end: `TamingCanceledEvent`, `TamingExpiredEvent` or `TamingFailedEvent`, after the refunds settled.

Before-events carry copied, read-only contexts. A canceled one spends nothing and answers `RefusedByMod`, and the same
token stays valid. While a
before-event runs, its attempt is guarded (19e 4.4): a handler that calls `Taming.Cancel` on it gets the cancel's answer
once the event is over, and the attempt then ends with nothing new spent (no step's cost, no bind); a handler's `Begin` on
the same target claims it first, and the `Begin` that posted the event answers `Busy`.

## Costs and the refund policy

A step's cost is exact and receipted ([item-costs.md](item-costs.md)): `Progress.Spent` lists every receipt.

| Policy | Cancel | Timeout | Failure (target gone, world ended, owner left, refused for good) | Tame |
|---|---|---|---|---|
| `NonRefundable` (default) | kept | kept | kept | kept |
| `RefundOnFail` | kept | kept | given back | kept |
| `Escrow` | given back | given back | given back | kept |

- **Refunds** go through `ItemCosts.Refund`, once per paid receipt, under a request id derived from the paid one: a
  second settlement can never refund twice. `Progress.Refunds` pairs each paid receipt with its refund's receipt. A refund
  the adapter refuses before it runs (its row went off) is a refused receipt there (`NotRun`, with the reason): the end
  still comes with its event and its answer.
- **A partial take** (the game took less of the stack than the step asked: disposition `Partial`) is never the step. It
  ends the attempt `PartialCostCommitted` with that receipt (spent as it says; the policy decides whether it comes back),
  and the rest is never taken again (19e 5.2). A gear wear another hook changed (`ModifiedByOtherMod`) is the use, once.
- **An uncertain receipt** (a fault inside the game's change) is never refunded or retried. It ends the attempt
  `UncertainCommit` with that receipt, and the policy still applies to the receipts that are certain.
- **A refund reaches only an owner in game, in the world the step was paid in.** An attempt that fails because its owner
  left (`OwnerLeft`), or ends with its world (`WorldEnded`), refunds nothing even with `Escrow` or `RefundOnFail`: the
  refund's receipt in `Progress.Refunds` says why (`StaleSession`, `WrongWorld`), and a mod that owes the player decides
  what to do (a later `Items.Give` when they are back, say). Kiln keeps no refund for a later session.
- **Not promised:** crash atomicity across the inventory and the pet record. A committed receipt means the inventory
  changed in the running world, nothing more.

## The tame and Kiln.Pets

A definition with `Pet` holds a slot of that pet definition from Begin. The slot counts against
`PetLimits.RetainedPerCharacter` and the roster's 128 records, with the other running tames. It is checked again before the
last step's cost: a full roster refuses (`Capacity`) before anything is taken.

At the commit, the owner's character gets a new record (`Pets.Grant`); that is the ownership commit, and it settles at the
roster's next step. Then, as the definition says, the tamed wild body is removed (`RemoveTarget`) and the pet is summoned
(`SummonPet`). Neither undoes the record. If the roster filled meanwhile, the answer is `CommitDeferred` and `Retry`
binds without a cost.

A definition with no pet binds nothing: the tame is the attempt's own result (`TamedEvent`) and the mod's handlers decide
what it means.

## The game's own taming (NativeTaming)

[docs/re/taming.md](../../re/taming.md) has the research.

A tameable wild animal eats a bait of its favourite template. Each **petting while it eats** is one step. At its required
steps the game makes it a farm animal (faction FarmAnimal, `IsTame`). Kiln hooks nothing in it and never erases a native
offer.

| Call or event | What it is |
|---|---|
| `NativeTaming.Read(creature)` | the animal's taming now: tameable, progress, required steps, phase (wild, pursuing, eating, tamed, dead), its bait and favourite bait template, sensor range, eat time |
| `NativeTaming.PlaceBait(creature)` | would place its own favourite bait. **Refused until proven** (`taming.native_bait` off, policy; KILNX002): a bait spawned from its template has no bait marker, the lab's simulated dog did not sense it, and a player could pick it up as a free item, so it throws and spawns nothing |
| `NativeTaming.PetStep(creature)` | one step made by the server: the animal plays its own synced pet sequence (the game's own, as a player's petting starts it; nothing is written), so the game may count the step itself (refused unless it eats its bait and needs steps). **Experimental (KILNX002)**: the refusals are lab-proven; that the game counts the step is not, so `Ok` means only that the sequence started (watch `NativeTamingProgressEvent`) |
| `NativeTamingProgressEvent`, `NativeTamedEvent`, `NativeBaitEatenEvent` | the watch, every 5 steps while anyone listens: progress (a step or a startle), the farm transformation, a bait eaten with or without a step |
| `NativeStep` in a definition | Kiln's attempt waits for the game's own count, so a mod chooses native, Kiln steps, or both |

What is tameable is registry data, the creatures registry's `tameable` (read only), `tamingSteps` (live: every animal of
the entry, at once) and `favoriteBait` (read only) fields ([registries/creatures.md](../modding/registries/creatures.md),
[docs/re/taming.md](../../re/taming.md) 8). A mod changes how many pettings an animal takes there, never through a game
member.

Who tamed a native animal is not known: the game records no player for a step.
`NativeTamingState.TamedBy` is always null, and `taming.native_observe` stays off until a lab with a signed-in player
shows where the petting player can be read.

## Tests

- Managed, offline (`managed/tests/Kiln.Taming.Tests`):
  - the engine against chapter 19e section 6's list: selections, replay, before-events and their order, finalization
    and the cost-free retry, timeouts and in-flight settlement, closed attempts, limits;
  - the refund policies, a bind that settles later, a world change;
  - the Pets binder;
  - the JSON compiler and its error paths;
  - NativeTaming over a made-up world.
- Lab (`mods/tests/item-costs-test`, `mods/tests/native-taming-test`, player-free): the adapter against the real game,
  and the game's taming with the server petting.
- Lab with a LABBOT bot player as the tamer (`mods/tests/taming-review-test`, 15 PASS, 0 FAIL on 11-27, server build
  f0d824be): a taming defined in `OnLoad` before any world, the `tamings` blocks of a data file, `PlaceBait` refused, Kiln's
  steps end to end with the pet summoned next to its owner and the wild body removed, an escrow refund given back once
  (`AlreadyRefunded` for a second request), a roster filled during the attempt refused before the last cost.
