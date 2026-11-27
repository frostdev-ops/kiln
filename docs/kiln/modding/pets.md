# Pets for mod authors

Since 0.2.0-dev.32. Kiln keeps pets (records, bodies, movement, protection, revive, the owner's HUD); **your mod decides
what a pet is**. Contract and every rule: [services/pets.md](../services/pets.md). A complete example:
`mods/samples/PetsSample`.

## Define a pet

In C# (`OnLoad`), as a block:

```csharp
// a game creature, by its creatures-registry id: asks nothing of players' games
var hound = Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf", Name = "Hound" });

// a creature block of your own, and every choice spelled out
var wolf = Blocks.Define("wolf", new CreatureBlock { Body = Bodies.Vukah });
Blocks.Define("duelist", new PetBlock
{
    Creature = wolf,
    Name = "Duelist",
    Level = new PetLevelPolicy { Mode = PetLevelMode.OwnerAtSummon },
    Movement = new PetMovementPolicy { FollowDistance = 4, AllowCatchUp = false },
    Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule, HostileFactions = [Faction.Vukah, Faction.Fogger] },
    Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Absent },
    Revive = new ReviveBlock { ChannelSeconds = 5, Cost = new ReviveCostBlock("Material_T1_Twigs", 3) },
    Caps = new PetCaps { LivePerCharacter = 2 },
    Hud = new PetHudPolicy { RecoveryMarker = false },
});
```

Or in `blocks/*.json`, section `pets`, the same fields:

```json
{
  "pets": {
    "hound": { "creature": "game:enemy_wildbeast_wolf", "name": "Hound" },
    "duelist": {
      "creature": "wolf", "name": "Duelist",
      "targets": { "pvp": "byModRule", "hostileFactions": ["vukah", "fogger"] },
      "downed": { "outcome": "absent" },
      "revive": { "channelSeconds": 5, "cost": { "item": "Material_T1_Twigs", "count": 3 } }
    }
  }
}
```

The pet's id is `<mod id>:<name>` (`Was` keeps it through a rename). Without Kiln.Blocks: `Pets.Define(new PetDefinition
{ Id = "my-mod:hound", Creature = "game:enemy_wildbeast_wolf" })`. A creature is always a Kiln id: never a template name
or a GUID. Only enemy creatures that walk can be pets in this Kiln. A revive cost's item is checked once the game's
content is ready: an item the game lacks (or gear) is your mod's error in kiln.log and the launcher, and that pet's revives
are refused (`CostUnavailable`) with nothing taken.

## Give, summon, order, revive, release

```csharp
var owner = CharacterData.KeyOf(player);              // the character
var given = await Pets.Grant(owner, "my-mod:hound", PetRequest.New());
var who = PlayerRef.Of(player);
await Pets.Summon(who, given.Pet!.Id, PetRequest.New());
await Pets.Command(who, given.Pet.Id, PetOrder.Stay, EntityRef.None, PetRequest.New());
await Pets.Command(who, given.Pet.Id, PetOrder.Attack, EntityRef.Of(enemy), PetRequest.New());   // checked against the pet's rule
await Pets.Revive(who, pet, PetRequest.New());        // stand at its recovery anchor
await Pets.Release(who, pet, PetRequest.New());
foreach (var p in Pets.Of(owner)) Log.Info($"{p.Label}: {p.Life} {p.Presence} {p.Health}/{p.HealthMax}");
```

Every answer is a `PetResult` (`Ok`, `Reason`, the pet after it, cost receipts). Requests run on the server's tick
thread; `await` them from your handlers or commands.

## Decide per pet

- `PetDowningEvent`: set `Outcome` to choose what this pet becomes (an anchor, absent, released).
- `PetRevivingEvent`: `Cancel()` to refuse a revive (a cooldown, a zone); nothing is taken.
- `Pets.SetHostile(a, b, true)`: your duel rule. Pets whose targets say `byModRule` may then fight the other character and
  that character's by-mod-rule pets. Declare it when both players agreed; end it with `false`. Pairs last for the world
  session.
- Attack, assist, defend: Kiln never attacks by itself. Order an attack when your rule says so (the owner was hit, a key
  was pressed); Kiln refuses targets the pet's rule protects (`TargetProtected`).

## The HUD

Kiln draws a pet bar for owners whose game runs Kiln. Per definition: `PetHudPolicy`. Per player:
`PetsClient.Configure(new PetHudOptions(Enabled: false))` and draw your own from `PetsClient.Snapshot` in a
`RenderFrameEvent` handler (read the snapshot once per frame; never act on the game from the render thread).

## What a player's game needs

A pet on a game creature: nothing (vanilla players see the creature; only Kiln clients see the bar). A pet on a creature
block with a body or animation of your own: what that creature block needs (`clients: mod`).
