# Pets for mod authors

Since 0.2.0-dev.32. Kiln keeps pets (records, bodies, movement, protection, revive, the owner's HUD); **your mod decides
what a pet is**. Contract and every rule: [services/pets.md](../services/pets.md). A complete example:
`mods/samples/PetsSample`.

## Define a pet

In C# (`OnLoad`), as a block:

```csharp
// a game creature, by its creatures-registry id: asks nothing of players' games
var hound = Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });

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
    "hound": { "creature": "game:enemy_wildbeast_wolf_earlygame", "name": "Hound" },
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
{ Id = "my-mod:hound", Creature = "game:enemy_wildbeast_wolf_earlygame" })`. A creature is always a Kiln id: never a
template name or a GUID. Only enemy creatures that walk can be pets in this Kiln. A revive cost's item is checked once
the game's content is ready: an item the game lacks (or gear) is your mod's error in kiln.log and the launcher, and that
pet's revives are refused (`CostUnavailable`) with nothing taken.

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

## Walking beside the owner: measured clearance

A pet walks routes beside its owner only when Kiln has a **measured clearance profile** for its creature's body, at its
scale, on the running game build ([the service page](../services/pets.md#measured-clearance-gate-c6)). Without one it
still follows, by catching up to its owner on safe ground. Kiln ships two kinds, and a game update invalidates both
until Kiln ships new ones:
- **creature profiles** for the game creatures it calibrated one by one (the list is on the service page);
- **body profiles**: when three or more walking creatures with the same body (path walker, movement collider, the
  template's own scale) were calibrated and every one passed with the same pair, every walking creature with that body
  gets it. These cover most of the game's walking enemies at scale 1 (the service page lists the bodies).

A game creature's id matches whatever its case (`game:Enemy_Vukah_Melee` is `game:enemy_vukah_melee`).

Your own creature block, or a game creature at a scale Kiln did not calibrate, needs a profile of your own (unless its
body has a body profile). The calibration runs on Kiln's own lab tooling, on a dedicated lab server (no game client), so
it needs what Kiln's developers use:
- a Kiln source checkout, on Linux ([Building on Linux](../building-on-linux.md): `prepare` builds Kiln and deploys it
  into the lab);
- the lab base: a vanilla SteamCMD install of the dedicated server in `~/srv/enshrouded/lab2` with a world in its
  `savegame` folder, and a GE-Proton Wine prefix in `~/srv/enshrouded/ge-pfx`
  ([Lab base](../linux-server.md#lab-base-srvenshroudedlab2); `tools/lab/lab.sh` says what it reads).

1. Build your mod, then calibrate in a lab of your own (a lowercase `<name>-<name>` and a free UDP port):
   ```
   tools/pets-clearance/calibrate.sh prepare --lab my-pets --port 32100 --creature my-mod:wolfling --mod path/to/built/my-mod --out .out/my-clearance
   tools/pets-clearance/calibrate.sh run --lab my-pets --out .out/my-clearance     # one lab session: 1 min, then 5 min a creature (2.5 with --no-native at prepare)
   tools/pets-clearance/calibrate.sh profile --lab my-pets --out .out/my-clearance
   ```
   For a game creature at another scale: `--creature game:enemy_wildbeast_wolf_earlygame@1.5`. `report.md` shows every
   fixture and requirement pair, and why a pair failed or does not vouch for the body. `run` stops at once when the lab
   server ends before the lab mod's last line.
2. Ship `.out/my-clearance/clearance.json` as `pets/clearance.json` in your mod's folder. It applies to your mod's pets
   only, and only on the game build it names. It may hold body profiles too, when you calibrated three or more creatures
   of one body.
3. Calibrate again after a game update (the profile names the build), and after you change the creature's `Body.Size`
   or `Body.Scale` (the profile names the body it was measured on; a running server notices a changed body within 10 s).

A creature for which no requirement pair passes gets no profile: a route it accepts somewhere would be one its body
cannot walk. Nor does a body wider than 2 m or taller than 3 m (the lab's widest gap and highest headroom), which the
fixtures cannot judge, nor a pair that leaves a gap or headroom one 0.5 m cell narrower or lower than the body
unmeasured. Its pets catch up instead.

## The HUD

Kiln draws a pet bar for owners whose game runs Kiln. Per definition: `PetHudPolicy`. Per player:
`PetsClient.Configure(new PetHudOptions(Enabled: false))` and draw your own from `PetsClient.Snapshot` in a
`RenderFrameEvent` handler (read the snapshot once per frame; never act on the game from the render thread).

## What a player's game needs

A pet on a game creature: nothing (vanilla players see the creature; only Kiln clients see the bar). A pet on a creature
block with a body or animation of your own: what that creature block needs (`clients: mod`).
