<!-- SPDX-License-Identifier: MIT -->
# Taming (since 0.2.0-dev.32)

Kiln makes taming possible: attempts, steps with exact costs and receipts, replay-safe requests, timeouts, events, and a
pet record when the tame commits. **What taming is in your mod is your choice**:
- which creatures;
- what each step costs;
- whether paid steps come back;
- how long an attempt may last;
- the pet it makes.

The service page is [services/taming.md](../services/taming.md); a whole mod is `mods/samples/TamingSample`.

## Declare a taming

C#, with ids from the registries (creatures by their creatures registry id, items by their `ItemId`):

```csharp
Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
Taming.Define(new TamingDefinition
{
    Id = "my-mod:wolf",
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [new WeakenStep(0.4), new FeedStep(Items.Lookup("Food_T1_raw_bad_meat_LeanMeat")!.Id, 2)],
    Pet = "my-mod:tame-wolf",
    Refund = TamingRefundPolicy.RefundOnFail,
});
```

JSON, the same definition (`Taming.DefineJson`; one object or an array):

```json
{ "id": "my-mod:wolf", "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "my-mod:tame-wolf",
  "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
             { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ],
  "rangeMeters": 3, "requireThresholdUntilCommit": true, "inactivitySeconds": 30, "maxAttemptSeconds": 300,
  "refund": "refundOnFail", "removeTarget": true, "summonPet": true }
```

Step kinds:

| Kind | Fields |
|---|---|
| `feed` | `item`, `count` (1 to 999, default 1) |
| `weaken` | `maxHealthFraction` (in (0, 1]) |
| `tool` | `item`, `wear` (0 to 32767, default 1), `allowBreak` (default false) |
| `native` | `steps` (1 to 255, default 1): the game's own count |

Rules for the JSON:
- **Items** are items registry ids:
  - `game:<debug name in lower case>`;
  - `game:<name>~<first 8 hex digits of its GUID>` when two items share a name;
  - `game:item_<8 hex>`;
  - or an item id `0x<hex>`.
- **A mod's own items** are named in C# by their `ItemId`.
- **Errors:** an unknown field, a wrong type or an unknown item is an `ArgumentException` naming the exact path
  (`$.steps[1].count`), and nothing is registered.
- **When:** a definition needs no world (a mod declares it in `OnLoad`), but these item ids resolve only once the game's
  items are readable (`Items.Available`).

## Drive an attempt

Every action sends your request id and the token of the last answer. Use a new id for each intended action, and the same
id when you resend the same action after a network hiccup.

```csharp
var r = Taming.Begin(player, EntityRef.Of(wolf), "my-mod:wolf", Guid.NewGuid());
var p = r.Progress!;                                        // the token: p.StepChallenge, p.Revision, p.CompletedSteps
r = await Taming.Advance(player, p.Id, new TamingToken(Guid.NewGuid(), p.StepChallenge, p.Revision, p.CompletedSteps));   // weaken: no selection
p = r.Progress!;
var pick = Taming.Choices(player, p.Id).Choices[0].Selection;   // the server's read of the slot the player chose
r = await Taming.Advance(player, p.Id, new TamingToken(Guid.NewGuid(), p.StepChallenge, p.Revision, p.CompletedSteps), pick);
if (r.Ok && r.Pet is { } pet) { /* tamed: pet is the new Kiln.Pets record */ }
else if (r.Reason?.StartsWith("CommitDeferred") == true) { /* every step paid: Taming.Retry later, nothing more is taken */ }
```

- **Feed and tool steps** take a selection: one slot, read by the server (`Taming.Choices`, or `ItemSelection.Of` of
  an `Items.Read` slot). Nothing searches when the cost runs. A slot that changed refuses `StaleSelection` and nothing
  is taken.
- **Weaken and native steps** take none.
- **The last step tames:** an Ok answer has `Pet` when the definition names one.

## Your choices, Kiln's mechanism

| You decide | Kiln does |
|---|---|
| the steps and their costs | takes each cost exactly from the selected slot, once per request id, and receipts it |
| `Refund`: keep paid steps (default), give them back on a failure that is not the player's, or hold them in escrow until the tame | settles the policy at the end, refunds each receipt once, never refunds an uncertain one |
| `InactivitySeconds`, `MaxAttemptSeconds`, `RangeMeters` | runs them in simulation time, settles a cost in flight before it ends an attempt |
| `Creatures` | refuses others (`TemplateMismatch`) before any cost |
| `Pet`, `RemoveTarget`, `SummonPet` | holds the roster slot from Begin, refuses a full roster before the last cost, grants the record, then removes the wild body and summons the pet |
| cancel `TamingBeginningEvent`, `TamingAdvancingEvent`, `TamingCommittingEvent` | spends nothing for a canceled step; the token stays valid |

## The game's own taming

A wild dog, cat, goat, bunny, catdeer, turkey, yak, capybara or sanddigger tames the game's way:
1. its favourite bait is placed near it, and it eats the bait;
2. a player pets it while it eats (one step, about 3.75 s);
3. at its required steps it becomes a farm animal.

Kiln does not change any of it. `NativeTaming.Read` shows where an animal is, the native events report progress and the
tame, and `NativeTaming.PlaceBait` and `NativeTaming.PetStep` (experimental, KILNX002: not yet proven to make the game count a step; opt in with `NoWarn` KILNX002) let a server mod drive it. A `NativeStep` in a definition
follows the game's count, so your taming can be the game's, Kiln's, or both. The game does not record who tamed an animal:
your mod decides that.
