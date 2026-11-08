# Interaction offers (C#)

An offer is the "E" prompt a player sees near an entity. `Kiln.Offers` (in `Kiln.Gameplay`) puts one on any
entity at run time and calls your handler when a player accepts it. It is the C# version of EMF's `offer`,
`offer_remove`, `spawn_offer` and `offer_active`.

Blocks (`world-blocks.md`, `interact`) give offers to your own props. Use `Offers` for anything else: a prop you
spawned, an existing world object without an offer, a creature.

## Example

```csharp
using Kiln;

var bell = await Offers.SpawnWithOffer("Prop_Deco_Steppeprops_Barrel_01", pos, "Ring the bell", 0, a =>
{
    Log.Info($"{a.Player} rang {a.Entity}");
});

Offers.Add(someEntity, null, OfferVerbs.Inspect, a => Log.Info($"{a.Player} looked"),
    new OfferOptions { RangeMeters = 4 });

if (Offers.IsActive(someEntity)) Offers.Remove(someEntity);
```

## API

All calls run on the tick thread, on a server.

| Call | What it does |
|---|---|
| `Offers.Add(entity, text, verb, handler, options?)` | Adds the offer. Throws `InvalidOperationException` with the reason when refused. |
| `Offers.TryAdd(entity, text, verb, handler, out reason, options?)` | The same, returns false and the reason. |
| `Offers.SpawnWithOffer(template, position, text, verb, handler, options?, spawn?)` | Spawns, then adds. |
| `Offers.Remove(entity)` | Takes your offer off. False if you have none there. |
| `Offers.IsActive(entity)` | You have an offer there (pending or active). |
| `Offers.Status(entity)` | `None`, `Pending`, `Active` or `Removing`. |
| `Offers.List()` | Every mod's offers (entity, owner, verb, accepts). |
| `Offers.Available` | False when the server's Kiln has no `component_change` (older kiln.gameplay). |

- `text` makes a custom prompt word. Its id is `Offers.CustomVerbId(text)` (the same id EMF uses). A client shows
  the text only if it knows that id; otherwise pass `text: null` and a game word from `OfferVerbs`.
- `OfferOptions`: `Sequence` (`OfferSequences.Touch` by default), `RangeMeters` (32 by default), `LowPriority`,
  `BuildMode`.
- The handler gets `OfferAccepted(Player, Entity)` and runs as your mod. An exception is logged in your log.

## Rules

- The prompt appears a step or two after `Add`; `Status` turns from `Pending` to `Active` then.
- One offer per entity. Refused: players, entities with the game's own offer (chests, benches, beds), entities
  with another mod's offer, and a different offer from you on the same entity (remove it first).
- Kiln adds `InteractionOfferRuntime`, `ClientInteractionOffer` and `CheckPlayerInRange`, and removes only the
  ones it added. If the game replaced the offer meanwhile, Kiln leaves it alone.
- When your mod unloads, Kiln removes its offers. Entities that go away drop their offers.
- Needs kiln.gameplay with `component_change` and kiln.world (`Offers.Available`).
