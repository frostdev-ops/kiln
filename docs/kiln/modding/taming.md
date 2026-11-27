<!-- SPDX-License-Identifier: MIT -->
# Taming (since 0.2.0-dev.32)

Kiln makes taming possible: attempts, steps with exact costs and receipts, replay-safe requests, timeouts, events, and a
pet record when the tame commits. **What taming is in your mod is your choice**:
- which creatures;
- what each step costs;
- whether paid steps come back;
- how long an attempt may last;
- the pet it makes.

The service page is [services/taming.md](../services/taming.md); a whole mod is `mods/samples/TamingSample`. What the pet is after
the tame: [pets.md](pets.md).

## Make it: a wolf the player tames in two steps

The player weakens a wolf to 40 % health, then feeds it two bait items. A server mod of one data file and one class.

1. Make the project ([mdk.md](mdk.md)): `dotnet new kiln-mod -n WolfTamer`, with `<KilnModId>wolf-tamer</KilnModId>` and
   `<KilnServices>Taming;Pets;Blocks;Items;Creatures;Players;Chat;World</KilnServices>` in the `.csproj`.
2. Declare the taming as data, in `blocks/taming.json`: the bait item, the pet and the taming itself.

   ```json
   {
     "format": 1,
     "items": { "wolf-bait": { "name": "Wolf Bait", "stack": 20 } },
     "pets": { "tame-wolf": { "creature": "game:enemy_wildbeast_wolf_earlygame", "name": "Tamed Wolf" } },
     "tamings": {
       "wolf": { "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "tame-wolf",
                 "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 }, { "kind": "feed", "item": "wolf-bait", "count": 2 } ],
                 "refund": "refundOnFail" } }
   }
   ```

3. Drive the attempt from commands. Each action carries a new request id and the token of the last answer
   ([the details](#drive-an-attempt)):

   ```csharp
   public sealed class WolfTamerMod : Mod
   {
       private readonly Dictionary<PlayerRef, TamingProgress> _attempts = [];

       [Command("tame begin", Description = "start taming the nearest wolf (3 m)")]
       private async Task Begin(CommandContext c)
       {
           var who = PlayerRef.Of(c.Source.Player);
           var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
           foreach (var wolf in Creatures.Near(at, 3))
           {
               var r = await Taming.Begin(who, EntityRef.Of(wolf), "wolf-tamer:wolf", TamingRequest.New());
               if (r.Refusal == TamingRefusal.TemplateMismatch) continue;                // not a wolf
               if (r.Progress is { } p) _attempts[who] = p;
               c.Reply(r.Ok ? "taming started: /tame step, then /tame feed" : $"refused: {r.Reason}");
               return;
           }
           c.Reply("no wolf within 3 m");
       }

       [Command("tame step", Description = "the step that takes no item (weaken)")]
       private async Task Step(CommandContext c)
       {
           var who = PlayerRef.Of(c.Source.Player);
           var p = _attempts[who];
           var r = await Taming.Advance(who, p.Id, TamingRequest.New(p.Token));
           if (r.Progress is { } next) _attempts[who] = next;
           c.Reply(r.Ok ? $"step {r.Progress!.CompletedSteps}/{r.Progress.TotalSteps}" : $"refused: {r.Reason}");
       }

       [Command("tame feed", Description = "pay the feeding step from the first slot that can")]
       private async Task Feed(CommandContext c)
       {
           var who = PlayerRef.Of(c.Source.Player);
           var p = _attempts[who];
           var choices = Taming.Choices(who, p.Id);
           if (!choices.Ok || choices.Choices.Count == 0) throw new CommandException(choices.Reason ?? "nothing pays this step");
           var r = await Taming.Advance(who, p.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
           if (r.Progress is { } next) _attempts[who] = next;
           c.Reply(r.Ok ? (r.Pet is { } pet ? $"tamed: your new pet {pet.Id}" : "fed") : $"refused: {r.Reason}");
       }
   }
   ```

4. Build and deploy, join, carry two `Wolf Bait` (your mod's item block: [item-blocks.md](item-blocks.md)), and stand next to a wolf you
   have hurt to under 40 %. `/tame begin`, `/tame step`, `/tame feed`: the wolf is taken from the world, and a Tamed Wolf stands next to you as a
   Kiln.Pets record. A failure that is not yours gives the paid bait back (`refundOnFail`).

Everything else on this page is the reference for the choices in step 2: which steps, what they cost, what is refunded, how long
an attempt lasts, what the pet is. The same choices from C# and every step kind follow.

## Declare a taming

C#, with ids from the registries (creatures by their creatures registry id, items by their `ItemId`):

```csharp
Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
var defined = Taming.Define(new TamingDefinition
{
    Id = "my-mod:wolf",
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [new WeakenStep(0.4), new FeedStep(Items.Lookup("Food_T1_raw_bad_meat_LeanMeat")!.Id, 2)],
    Pet = "my-mod:tame-wolf",
    Refund = TamingRefundPolicy.RefundOnFail,
});
if (!defined.Ok) Log.Warn($"{defined.Refusal}: {defined.Reason}");   // DefinitionInUse: attempts of the old one run
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
- **A mod's own items** are named by a `TamingBlock` (below), or in C# by their `ItemId`.
- **Errors:** an unknown field, a wrong type or an unknown item is an `ArgumentException` naming the exact path
  (`$.steps[1].count`), and nothing is registered.
- **When:** a definition needs no world (a mod declares it in `OnLoad`), but these item ids resolve only once the game's
  items are readable (`Items.Available`).

### As a block (`TamingBlock`, blocks/*.json `tamings`)

A `TamingBlock` is the same definition written as a block: items, creatures and pets are named as every block names
them, so a mod's own items work in JSON too. It compiles to the `TamingDefinition` above with the id
`<mod id>:<name>`, which `Taming.Begin` takes.

```json
{ "format": 1,
  "items": { "wolf-bait": { "name": "Wolf Bait", "stack": 20 } },
  "pets": { "tame-wolf": { "creature": "game:enemy_wildbeast_wolf_earlygame", "name": "Tamed Wolf" } },
  "tamings": {
    "wolf": { "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "tame-wolf",
              "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
                         { "kind": "feed", "item": "wolf-bait", "count": 2 } ],
              "refund": "refundOnFail" } } }
```

```csharp
var bait = Blocks.Define("wolf-bait", new ItemBlock { Name = "Wolf Bait", Stack = 20 });
var pet = Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Tamed Wolf" });
Blocks.Define("wolf", new TamingBlock
{
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
    Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed(bait, 2)],
    Pet = (BlockHandle<PetBlock>)pet,
    Refund = TamingRefundPolicy.RefundOnFail,
});
```

- **Items:** a game item (`Food_T2_Dog_Bait`, a `GameItems` constant) or an item block of this mod (its name) or of
  another (`other-mod:name`). A feed step takes a plain stack item, a tool step a gear item.
- **Creatures:** each named as a `PetBlock`'s `Creature` is (`PartRef<CreatureBlock>`): a game creature by its creatures
  registry id (`game:...`, a trailing `*` for a prefix of game ids), or a creature block (its handle, its name,
  `other-mod:name`, or one written in place).
- **Pet:** a pet block (inline, its name, `other-mod:name`), or the id of a pet a mod defined in code.
- **Errors** are `BlockError`s naming the field (`steps[1].count`). An item the game's catalog does not have while the
  block compiles (a game client's `OnLoad`, the SDK's bake, a dedicated server before Kiln.Items reads the catalog) is
  checked once content is ready and the catalog is readable (Kiln waits up to about 2 minutes for it); if it is still
  unknown then, the taming is not defined (its `Begin` answers `DefinitionUnknown`) and the mod's problem says why.
- It asks nothing of players' games (server only).

## Drive an attempt

Every action sends a `TamingRequest`: your request id and the token of the last answer (`progress.Token`,
`choices.Token`). `TamingRequest.New(token)` makes a new id for each intended action; send the same request again when you
resend the same action after a network hiccup. It is the same shape as Kiln.Pets' `PetRequest`.

```csharp
var r = await Taming.Begin(player, EntityRef.Of(wolf), "my-mod:wolf", TamingRequest.New());
var p = r.Progress!;                                        // p.Token: the challenge, the revision and the step
r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));   // weaken: no selection
p = r.Progress!;
var choices = Taming.Choices(player, p.Id);
var pick = choices.Choices[0].Selection;                    // the server's read of the slot the player chose
r = await Taming.Advance(player, p.Id, TamingRequest.New(choices.Token), pick);
if (r.Ok && r.Pet is { } pet) { /* tamed: pet is the new Kiln.Pets record */ }
else if (r.Refusal == TamingRefusal.CommitDeferred) { /* every step paid: Taming.Retry later, nothing more is taken */ }
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
| `InactivityTimeout`, `MaxAttemptDuration` (JSON `inactivitySeconds`, `maxAttemptSeconds`), `RangeMeters` | runs them in simulation time, settles a cost in flight before it ends an attempt |
| `Creatures` | refuses others (`TemplateMismatch`) before any cost |
| `Pet`, `RemoveTarget`, `SummonPet` | holds the roster slot from Begin, refuses a full roster before the last cost, grants the record, then removes the wild body and summons the pet |
| cancel `TamingBeginningEvent`, `TamingAdvancingEvent`, `TamingCommittingEvent` | spends nothing for a canceled step; the token stays valid |

## The game's own taming

A wild dog, cat, goat, bunny, catdeer, turkey, yak, capybara or sanddigger tames the game's way:
1. its favourite bait is placed near it, and it eats the bait;
2. a player pets it while it eats (one step, about 3.75 s);
3. at its required steps it becomes a farm animal.

Kiln does not change any of it. `NativeTaming.Read` shows where an animal is, the native events report progress and the
tame, and `NativeTaming.PetStep` (experimental, KILNX002: not yet proven to make the game count a step; opt in with
`NoWarn` KILNX002) lets a server mod drive it. `NativeTaming.PlaceBait` is refused until a lab proves a bait the game
senses (`taming.native_bait` off): today the player places the bait with the game's own bait item. A `NativeStep` in a definition
follows the game's count, so your taming can be the game's, Kiln's, or both. The game does not record who tamed an animal:
your mod decides that.
