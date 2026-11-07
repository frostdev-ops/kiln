# Wiki note: building blocks, what's new

For the public guide (Building blocks pages). Suggested text below each heading; the modding docs have the details
(`docs/kiln/modding/blocks.md`, `item-blocks.md`, `world-blocks.md`, `sounds.md`).

## Status and perk icons of your own
A status or a perk can now have a PNG icon of its own:
```csharp
Blocks.Define("chill", new StatusBlock { Name = "Chill", Seconds = 6, Look = StatusLook.Frost, Icon = "icons/chill.png", OnTick = [...] });
Blocks.Define("ember-edge", new PerkBlock { Name = "Ember Edge", Icon = "icons/ember.png", Modifies = { [Stat.Strength] = 3 } });
```
Square, 256x256 like the game's (at most 1024x1024). The mod then needs `clients: mod`. Until a player's game has the
picture ready, the buff bar shows the look's icon, never a missing one. A game status's icon (`Icon = GameBuffs.…`)
still works as before.

## Tools of every kind
`ToolBlock.Harvests` now takes every tool the game has: axe, pickaxe, rake, mining sieve, glider, grappling hook,
fishing rod and torch. Each takes the numbers it has: a rake's `Power` and `Range`, a hook's `Range`, a rod's
`Fishing { Strength, Endurance }`, a torch's `Durability` (seconds it burns) and, for a torch or a harvesting tool,
`BreaksInto` (what it becomes when its durability runs out). A field a tool has no number for is an error that says why.

## Projectiles that pierce
`ProjectileBlock.Pierce = 3` flies through three creatures, running `OnHit` on each once, and stops at the fourth or at
anything that isn't a creature, where `OnImpact` runs.

## Props with your own model and collision
`Visual.Model = "models/lantern.glb"` in a prop: `kiln cook` cooks the glTF by itself (from `blocks/*.json`). Its
collision is the box around the model, or `Collider = new BoxCollider { Size = … }`. Interactions (`OnInteract`) work
as for furniture.

## Stations with a workshop of their own
(Already shipped, worth a line:) a station that works on its own and lists `Recipes` has a workshop and a menu of its
own: it runs only its recipes, its menu lists only them, titled with its name.

## Sounds as blocks
```jsonc
"sounds": { "frost-hum": { "clips": ["audio/frost_hum.wav"], "volume": 0.6, "range": [3, 40], "loop": true } }
```
`kiln cook` writes the cook steps for you and cooks them. Effects (`"sound": "frost-hum"`), stations' running sounds and
code play a sound block by its name. Needs `clients: mod`.

## A VFX on a look
`VisualBlock.Vfx` puts a VFX on the item in the hand (right hand, at the grip; `Offset` moves it up the blade), or on a
placed thing, like a lamp's light.

## Caveat for the guide
None of these has been seen in a real game client yet: they are checked on a dedicated server. Say "new, not yet
proven in game" until the manual test passes.
