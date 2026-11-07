# Effects: particles, lights and the game's VFX

Write an effect from nothing, in `blocks/*.json` or in C#, and attach it to whatever starts it. No compiler, no game data, no GUIDs. Or use one of the game's by name.

**Status.** The files, the budget and the checks are proven offline, and the effects have run on real GPUs in a test harness. Nobody has seen a Kiln-built effect in the game yet, so treat how it looks there as unproven, and say "needs a client check" until you've seen it. [What's proven](../services/vfx-cook.md).

## Start
```sh
kiln new vfx MyFx --server <server>
cd MyFx
kiln build && kiln deploy
```
You get `blocks/spark.json`, the same effect in C#, a small PNG, and keys to play both: F7 and Shift+F7 in a client, 3 m ahead of you. A dedicated server draws nothing. `mods/samples/VfxSample` is a bigger example: two particle blocks with a flipbook, a light, a game VFX and a spell.

## Write one
```jsonc
{ "format": 1,
  "vfx": {
    "frost_burst": {
      "particles": [ {
        "burst": 24, "lifetime": [0.4, 0.9], "speed": [2, 5],
        "spread": { "cone": 35 }, "gravity": -4,
        "size": { "linear": [0.15, 0] },
        "color": ["#ffffff", "#7fd4ff"], "alpha": { "linear": [1, 0] },
        "texture": "vfx/shard.png", "blend": "additive"
      } ],
      "light": { "color": "#7fd4ff", "radius": 6, "brightness": 4, "lifetime": 0.6, "fadeOut": 0.5 }
    } } }
```
The same in C#, with `<KilnServices>Blocks</KilnServices>`:
```csharp
var burst = Blocks.Define("frost_burst", new VfxBlock
{
    Particles =
    [
        new ParticleBlock
        {
            Burst = 24, Lifetime = (0.4f, 0.9f), Speed = (2f, 5f), Spread = Shape.Cone(35), Gravity = -4f,
            Size = Curve.Linear(0.15f, 0f), Color = Gradient.Of("#ffffff", "#7fd4ff"), Alpha = Curve.Linear(1f, 0f),
            Texture = "vfx/shard.png", Blend = Blend.Additive,
        },
    ],
    Light = new LightBlock { Color = "#7fd4ff", Radius = 6f, Brightness = 4f, Lifetime = 0.6f, FadeOut = 0.5f },
});
```
- **Prefer data.** A block in a file is there from the first start. The game lists its VFX once, so one from code is there from the next.
- **Every field:** [schemas/kiln-vfx.md](schemas/kiln-vfx.md).
- **A burst or a rate.** A particle block needs one. `burst` throws them once; `rate` keeps going until the effect stops, unless you give it a `duration`.
- **Curves** (`size`, `alpha`) are a number, `{"linear": [start, end]}`, a list, or `{"keys": [[t, value], ...]}`. **Colours** are one, a list, or keys.
- **Renames.** Ids come from your mod's id and the block's name. After a rename say `"was": "old_name"`.

### Where particles start, and which way they go
`"shape"` places each new particle in the effect's space (its scale and rotation apply): `{"sphere": 2}` (add `"inner": 1` for a hollow
one), `{"shell": 2}`, `{"hemisphere": 2}`, `{"box": [2, 1, 2]}`, `{"disc": 1.5}`, `{"ring": 1.5}`, `{"line": 4}`. `"spread": "outward"` sends them
away from the shape's centre: out of a sphere, along a disc's plane, across a line. C#: `Shape = SpawnShape.Ring(1.5f), Spread = Shape.Outward`.

`"align": "velocity"` turns a camera-facing quad to point the way it moves, and `"stretch": 0.05` adds 5 cm of length per m/s: sparks, rain,
streaks.
```jsonc
"spark_ring": { "particles": [ {
  "burst": 48, "lifetime": [0.4, 0.8], "speed": [5, 8], "shape": { "ring": 1 }, "spread": "outward", "gravity": -6, "drag": 1,
  "align": "velocity", "stretch": 0.05, "size": { "linear": [0.06, 0.02] }, "color": ["#ffffff", "#ffc060"], "intensity": 3,
  "texture": "vfx/spark.png", "blend": "additive", "emissive": 1 } ] }
```

### Per-play parameters
Every Kiln VFX takes `color` (`"#rrggbb"` or `"#rrggbbaa"`: a tint, and its alpha multiplies alpha), `size`, `speed`, `brightness` (particles and
light) and `count` (burst and rate). They are factors on what the block says, from 0 to 16, and white or 1 when nobody sets them. So one block
serves a blue frost and a red ember, a small and a large burst:
```jsonc
"statuses": { "glowing": { "name": "Glowing", "seconds": 6, "vfx": { "name": "warm_glow", "parameters": { "color": "#80c0ff", "brightness": 1.5 } } } }
```
```csharp
Vfx.PlayAt(burst, at, parameters: new VfxParams().Set("color", new Color(64, 160, 255)).Set("size", 2f).Set("count", 2f));
Vfx.Set(handle, "brightness", 0.5f);   // on a running one: colour, size and brightness change at once, count and speed for what spawns next
```
A cast, a status and an effect take them on a Kiln VFX; a projectile's or a prop's VFX can't yet (its template has no parameter component). A
name or a type that isn't one of the five is an error that names the field.

### Your own particle code
Advanced: [shaders.md](shaders.md). A block's particle can run a module you wrote in HLSL against Kiln's kit (`"shader": "shaders/build/swirl.spv"`).

## Textures
A PNG in the mod's `vfx/` folder, both sides a power of two, from 4 to 2048. Kiln compresses it at boot and keeps the result. Draw light on black for `"blend": "additive"`, and use alpha for `"blend": "alpha"`. A white texture takes its colour from `color`. A particle can also take its texture, tint and light from a [material](materials.md) of its mod: `"material": "frost_steel"`.

`"flipbook": { "columns": 2, "rows": 2 }` plays a grid of frames, once over a particle's life, or at `fps`. The PNG has to divide evenly into the grid.

## Attach it
A `Vfx` field takes what `Define` returned or a block's name. [blocks.md](blocks.md#vfx) has the table of what each field becomes.
```jsonc
{ "format": 1,
  "effects": { "boom": { "vfx": "frost_burst", "damage": { "ice": 10 } } },
  "statuses": { "chilled": { "name": "Chilled", "seconds": 8, "vfx": { "name": "frost_aura", "slot": "chest" } } } }
```
From code: `Vfx.PlayAt(burst, position)`, `Vfx.PlayOn(burst, entity, Slot.HandR)`, `Vfx.Stop(handle)` ([services/vfx.md](../services/vfx.md)). Data is better for what every player must see, since code only reaches Kiln clients near it, and a joiner doesn't see what started before.

## The game's VFX
Write `"game:<name>"`, or `VfxRef.Game("<name>")` and `VfxId.Game("<name>")` in C#. VFX have no names of their own, so the name is a template that carries it or a sequence that plays it. Nothing from the game goes into your mod, and vanilla VFX need no `clients: "mod"`.
```sh
kiln catalog build                               # once, from your own game
kiln catalog search fireball --section vfx       # find one by what uses it
kiln vfx info Explosion_Player_Spell_Fireball_1  # how long it runs, what it takes
```
- **Some VFX only have a GUID.** The catalog names every VFX, but entries without `byName` have only a label that nothing finds at run time: write `"game:<guid>"`. `kiln lint` and VS Code do this for you.
- **In C#,** `<KilnGameIds>Vfx</KilnGameIds>` adds `GameVfx.*` constants: GUIDs, named after what uses them. Without a catalog they don't exist, so a project others build should use strings.

**Parameters.** Some game VFX take named values. Only an effect, an area or an on-hit can set them, and only on one of the game's:
```jsonc
"orange_flash": { "vfx": { "name": "game:VFX_Dummy_Impact", "parameters": { "radius": 2, "color": "#ff9040" } } }
```
A number is a float (`3` too), `{"int": 7}` an int, `[x, y, z]` a float3, `"#rrggbb"` or `"#rrggbbaa"` a colour. **The game ignores a name or type its VFX doesn't have, without a word**, so check with `kiln lint`. Most parameters have no known name, because the game stores only hashes.

An effect with parameters is a play relayed from the server: Kiln clients near it see it, and a joiner doesn't.

## What players need
A new VFX has to be on every player's game: `side` both and `clients` `mod`. The server never sends assets. The build works this out for blocks, and [KL211](lint.md#kl211-clients-vfx) checks it. A mod that only names the game's VFX doesn't need that. See [blocks.md](blocks.md#what-players-games-need).

## Limits
All mods share what the game has left. Past a limit the game would abort, so Kiln refuses the mod that reaches it, and none of its VFX load.

| | Limit | The game uses | Left for all mods |
|---|---|---|---|
| VFX | 2,048 | 1,575 | 473 |
| Meshes | 1,024 | 638 | 386 |
| Mesh memory | 17,825,792 B | 17,567,564 B | about 240 KB |
| Sounds in VFX | 16 | 6 | 10 |
| Entries in one VFX | 55 | | 2 per particle block, 1 for a light |
| Particles alive in one block | 4,096 | | |

`kiln lint --profile <profile>` counts a whole mod list. The VS Code hover on a block's name shows what your mod uses.

## Check your work
| Tool | Says |
|---|---|
| `kiln lint` | The schema's mistakes, and [KL211](lint.md#kl211-clients-vfx), [KL314](lint.md#kl314-block-reference), [KL320](lint.md#kl320-vfx-budget) to [KL324](lint.md#kl324-vfx-flipbook): clients, names, the budget, parameters, textures, flipbooks |
| VS Code | Completion and hover for fields, game VFX and parameters, the budget on a block's name, snippets (`vfx-burst`, `vfx-trail`, `vfx-light`, `vfx-flipbook`, `vfx-game`, `vfx-parameters`). [vscode.md](vscode.md) |
| `kiln blocks show <name>` | What a block became, on a running server |
| `kiln-vfxcook check --mod <built mod>` | The builder's own report, offline, from a Kiln checkout ([details](../services/vfx-cook.md#kiln-vfxcook)) |

In `kiln.log`, at the game's start:
```text
vfx: built N Kiln VFX in T ms: VFX 1575+N of 2048, meshes 638+1 of 1024 (B of 17825792 bytes free), sounds 6+K of 16, curves 8+0 of 16; fingerprint ok
```
A mistake names the block and the field: `my-fx: blocks/x.json: vfx.spark.particles[0].maxCount: 4097 is outside 1..4096`. A wrong block registers nothing, and the file's other blocks still load.

**If something goes wrong,** `kiln/config/vfx.json` with `{"enabled": false}` skips mods' VFX before the game can see them, and `"skipMods": ["my-fx"]` skips one mod's.

## Not yet
A VFX block's own sound, decals, fog, custom meshes, named parameters of your own (the five above are every Kiln VFX's), a particle that draws a
model, and surface shaders for models (`docs/re/shaders.md` has the plan).
