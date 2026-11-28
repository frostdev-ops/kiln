# The mod API: taming (Kiln.Taming, dev.32+)

Part of the mod API guides ([api.md](api.md) is the index). Here: declaring a taming, driving an attempt, what Kiln settles
for you (exact costs, receipts, refunds, timeouts), binding a tame to a pet, and the game's own wildlife taming. The pet a tame
makes is [api-pets.md](api-pets.md); the exact-cost adapter under it is
[api-items.md](api-items.md#exact-costs-and-receipts-dev32). Grep [api-index/Taming.md](api-index/Taming.md) (and
`Blocks-pets.md` for `TamingBlock`) before you write a signature.

**Kiln makes taming possible; your mod decides what taming is.** Kiln runs the attempts server-side: it claims the target,
takes each cost exactly from the slot the player picked, receipts it, replays a retried request instead of paying twice, times
the attempt out in simulation time, settles refunds and grants the pet record. You choose the creatures, the steps and their
costs, the refund policy, the times and the pet.

- Assembly: `Taming` (and `Pets`, `Items`, `Blocks` as you use them). Server side; a joined client has no attempts.
- Pages: `docs/kiln/modding/taming.md`, `docs/kiln/services/taming.md`, wiki https://frostdev.io/kiln/wiki/taming. Sample:
  `TamingSample` (`/tame begin|step|feed|retry|cancel|status`).
- Status: dev.32 beta. Ran on a dedicated lab server with a bot (15 checks), not with a client: the natural bait flow, who
  tamed what, the client's progress display and refunds were not seen in a real game.

## Declare a taming

Three ways, one definition. The id is `<mod id>:<name>`, lower case; `Taming.Begin` takes it.

```csharp
// 1. data: a TamingBlock (blocks/*.json section "tamings"), items and pets named as blocks name them
var bait = Blocks.Define("wolf-bait", new ItemBlock { Name = "Wolf Bait", Stack = 20 });
var pet = Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
Blocks.Define("wolf", new TamingBlock
{
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed(bait, 2)],
    Pet = (BlockHandle<PetBlock>)pet,
    Refund = TamingRefundPolicy.RefundOnFail,
});

// 2. code: a TamingDefinition (game item ids resolve once Items.Available); Kiln keeps its own copy
var defined = Taming.Define(new TamingDefinition
{
    Id = "my-mod:dog", Creatures = ["game:animal_wildlife_dog_*"],
    Steps = [new NativeStep(1), new NativeStep(1)],
    InactivityTimeout = TimeSpan.FromMinutes(2), MaxAttemptDuration = TimeSpan.FromMinutes(10),
});
if (!defined.Ok) Log.Warn($"taming not defined: {defined.Reason}");   // DefinitionInUse: attempts of the old one run

// 3. JSON text: Taming.DefineJson(json) takes one object or an array
Taming.DefineJson("""
  { "id": "my-mod:wolf2", "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "my-mod:tame-wolf",
    "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
               { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ],
    "refund": "refundOnFail" }
  """);
```

| Member | Default and range |
|---|---|
| `Creatures` | game creatures by creatures-registry id (a trailing `*` takes a prefix: `game:animal_wildlife_dog_*`) or a creature block; others are refused `TemplateMismatch` before any cost |
| `Steps` (1 to 64, in order, the last one tames) | `FeedStep(ItemId, count 1..999)` a plain stack item; `WeakenStep(maxHealthFraction in (0, 1])`: no cost, the target's health must be at most that; `UseToolStep(ItemId, wear 0..32767, allowBreak)` a gear item; `NativeStep(1..255)` the game's own count (below). JSON kinds: `feed`, `weaken`, `tool`, `native` |
| `RangeMeters` | 3 (1 to 8): how close the player must be |
| `InactivityTimeout`, `MaxAttemptDuration` | 30 s (1 to 300 s), 300 s (inactivity to 3600 s); simulation time. TimeSpans in C#, numbers of seconds in JSON (`inactivitySeconds`, `maxAttemptSeconds`) |
| `Refund` | `NonRefundable` (default: paid steps stay paid). `RefundOnFail`: a failure that is not the player's (the body gone, the world ended, the owner left) gives paid steps back; a cancel or a timeout does not. `Escrow`: as `RefundOnFail`, and a cancel or a timeout gives them back too |
| `RequireThresholdUntilCommit` | true: the latest weaken threshold must hold at the tame |
| `Pet` | the id of a pet definition ([api-pets.md](api-pets.md)); null: no record |
| `RemoveTarget`, `SummonPet` | true, true: with a pet, the wild body goes once the record exists and the pet comes out |

A JSON item id is `game:<debug name lower case>` (`game:<name>~<8 hex>` when two share a name, `game:item_<8 hex>`, or
`0x<hex>`); an unknown field, a wrong type or an unknown item is an `ArgumentException` naming the path (`$.steps[1].count`) and
nothing registers. A mod's own items are named by a `TamingBlock` (or by `ItemId` in C#). **A definition needs no world**
(declare it in `OnLoad`), but game item ids resolve only once the game's items are readable: define from a tick where
`Items.Available` is true, as `TamingSample` does, or use a `TamingBlock` (Kiln waits up to about 2 minutes for the catalog;
if it is still unknown the taming is not defined and `Begin` answers `DefinitionUnknown`).

## Drive an attempt

Every action carries a **`TamingRequest`**: **your request id** and **the token of the last answer** (`TamingRequest.New(progress.Token)`;
the same request shape as Kiln.Pets' `PetRequest`). A new request for each intended action; the same request when you resend
after a hiccup (it can never pay twice).

```csharp
if (!Capabilities.Is(Taming.Capability)) return;                         // "taming.steps": a server world runs
var r = await Taming.Begin(player, EntityRef.Of(wolf), "my-mod:wolf", TamingRequest.New());   // a begin takes no token
if (!r.Ok || r.Progress is not { } p) { Log.Info(r.Reason ?? "refused"); return; }     // Busy, TemplateMismatch, OutOfRange, DefinitionUnknown, ...
r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));      // weaken: no selection; p.Token is the last answer's
if (!r.Ok || r.Progress is not { } q) { Log.Info(r.Reason ?? "refused"); return; }
var choices = Taming.Choices(player, q.Id);                               // slots the server read, and the view's token
r = await Taming.Advance(player, q.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
if (r.Ok && r.Pet is { } tamed) Log.Info($"tamed: {tamed.Id}");            // the new Kiln.Pets record
else if (r.Refusal == TamingRefusal.CommitDeferred) { /* every step paid: Taming.Retry later; nothing more is taken */ }
```

- **`Begin`, `Advance`, `Retry` and `Cancel` all return `Task<TamingResult>`** (a begin's is complete at once: it takes no
  cost). Server tick thread; `await` them.
  `Begin` claims the target (another attempt is `Busy`) and holds a roster slot for the pet.
- **Feed and tool steps take a selection**: one slot, read by the server (`Taming.Choices`, or `ItemSelection.Of` of an
  `Items.Read` slot). Nothing searches when the cost runs; a slot that changed is `StaleSelection` and **nothing** is taken.
  A client never names a slot id: build a picker from `Choices` and send back which row.
- **Weaken and native steps take none.**
- **The token rotates.** Keep the last `TamingResult.Progress`; the next request is `TamingRequest.New(progress.Token)` (its
  `StepChallenge`, `Revision`, `CompletedSteps`). A stale one is `StaleStep` / `StaleSession`, with nothing taken.
  `Taming.Cancel` works by attempt id: `TamingRequest.New()` (no token) cancels from a stale view; with a token it cancels only
  while that token is current. `Taming.Inspect(id)` reads the attempt; it is gone 10 simulation minutes after it closed.
- **`Choices` lists the first 256 slots** and sets `Truncated` when more could pay (never a refusal for many slots).
- **Decide by `r.Refusal`** (`TamingRefusal`, the same words as `PetRefusal` for the same meanings; `Reason` is text for
  logs): `Busy`, `NotOwner`, `OwnerOffline`, `StaleSession`, `StaleStep`, `RequestConflict`, `StaleSelection`, `OutOfRange`,
  `TemplateMismatch`, `DefinitionUnknown`, `Capacity`, `RefusedByMod`, `CommitDeferred`, ... (`Other` for a word a later Kiln
  adds).
- **Rows:** `kiln.taming` for `Taming.Define` and `DefineJson` (a definition is data: made when your mod loads, before any
  world runs); `taming.steps` for the attempts (on while a server world runs), and an attempt also needs the rows of its
  definition's steps: `items.costs.exact` (feed, and any refund), `items.costs.wear` (tool), `taming.native` (native),
  `pets.records` (names a pet). `Taming.Begin` throws `KilnUnavailableException` naming the one that is off, `Advance` for
  its step's.
- **A tame that cannot bind yet** (a full roster, a transient refusal) leaves the attempt `AwaitingCommit` with
  `CommitDeferred`: every step is paid, `Taming.Retry` takes nothing more and tries again.

## Your choices, Kiln's mechanism

| You decide | Kiln does |
|---|---|
| the steps and their costs | takes each cost exactly from the selected slot, once per request id, and receipts it |
| `Refund` | settles the policy at the end, refunds each receipt once, never an uncertain one |
| the times and range | runs them in simulation time, settles a cost in flight before it ends an attempt |
| `Creatures` | refuses others before any cost |
| `Pet`, `RemoveTarget`, `SummonPet` | holds the roster slot from Begin, refuses a full roster before the last cost, grants the record, then removes the wild body and summons the pet |
| cancel in `TamingBeginningEvent`, `TamingAdvancingEvent`, `TamingCommittingEvent` | spends nothing for a canceled step; the token stays valid |

Events (server tick thread): `TamingStartedEvent`, `TamingStepChangedEvent`, `TamedEvent` (the one ownership event: `Pet`),
`TamingCompletedEvent`, `TamingCommitDeferredEvent`, `TamingFailedEvent` / `TamingCanceledEvent` / `TamingExpiredEvent` (each
with `After.Refunds`). All have `After`, `Before`, `Cause` (a word: Expired, BodyGone, OwnerLeft, WorldEnded, ...) and `Receipts`.
A refund goes back to the same owner in the same world: for a player who has left, it is not settled yet, so do not promise one.

## The game's own taming

A wild dog, cat, goat, bunny, catdeer, turkey, yak, capybara or sanddigger tames the game's way: its favourite bait is placed
near it and it eats it; a player pets it while it eats (one step, about 3.75 s); at its required steps it becomes a farm animal.
Kiln changes none of that.

- `NativeTaming.Read(entity)` says where an animal is (`NativeTamingState`: `Phase`, `Progress`, `Required`, `Bait`,
  `BaitTemplate` (the favourite bait's template Guid), `EatDuration`); `NativeTamedEvent`, `NativeTamingProgressEvent`, `NativeBaitEatenEvent`
  report it. Ask `NativeTaming.Available` (`taming.native`).
- A `NativeStep` in a definition follows the game's count, so a taming can be the game's, Kiln's, or both. The game does **not**
  record who tamed an animal: `NativeTamingState.TamedBy` is always null on this build (`taming.native_observe` is off);
  your mod decides who gets credit.
- `NativeTaming.PetStep` is experimental (KILNX002, not proven to make the game count a step). `NativeTaming.PlaceBait` is
  refused (`taming.native_bait` off): today the player places the game's own bait item.
- The registry can edit it: `creatures.tamingSteps` (and `Tameable`, `FavoriteBait` read only) per creature
  ([api-registry.md](api-registry.md)).

## Tests

`KilnHarness.Taming` (dev.32+, [testing.md](testing.md)) runs Kiln's real attempt engine on the kit's world: `kiln.Taming`'s
`Join` and `Place` make the player and the creature, `Give` items, `Hurt` it, then `Taming.Begin`, `Advance`, `Retry` and `Cancel` in
`kiln.Run`, with exact stack takes and refunds on the kit's inventories; tamings your `OnLoad` defined stay. It wears no tools
and runs none of the game's own taming (`NativeStep` counts the steps the test makes): check those, and the flow with a
player, on a local server with commands like `TamingSample`'s, reading `kiln.log` (`taming:` lines) and each `r.Refusal`.
