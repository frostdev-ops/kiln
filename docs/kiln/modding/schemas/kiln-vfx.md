# Kiln VFX blocks: the "vfx" section of blocks/*.json

<!-- Generated from schema/kiln-vfx.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Effects written from nothing: particles, a light. The same shape as the C# records. No compiler, no game data.

## Top level

## block

One effect. It needs particles, a light, or both.

| Key | Type | Required | Notes |
|---|---|---|---|
| `cullDistance` | number or [positiveRange](#positiverange) |  | Metres. Beyond this nothing is simulated or drawn. A number is the far distance; [near, far] gives both. Default [50, 75]. |
| `gameSpriteModule` | boolean |  | A diagnostic: draw the particles with one of the game's own sprite modules, to tell a problem in the data from one in Kiln's module. It takes the first colour and size, the texture and the mesh; the rest is baked into that module. |
| `light` | [light](#light) |  |  |
| `particles` | array of [particle](#particle) |  | What spawns and how it looks. At most 27 with a light, 27 without (each takes two of a VFX's 55 entries). max items 27 |
| `sound` | string |  | A sound's name. Not played yet: counted in the budget of 10 new sounds in VFX. min length 1 |
| `was` | string |  | The block's old name: the id stays the old one, so nothing that names this effect notices the rename. min length 1 |

## colour

Pattern `^#?([0-9a-fA-F]{3}\|[0-9a-fA-F]{6})$`.

Type: string

## curve

A number; {"linear": [start, end]}; a list of evenly spaced values; or {"keys": [[t, value], ...]}. Sampled to 8 values.

Type: number or array of number or object or object

## gradient

One colour; a list of evenly spaced colours; or {"keys": [[t, "#rrggbb"], ...]}.

Type: [colour](#colour) or array of [colour](#colour) or object

## keys

min items 1

Type: array of array

## light

One point light for the life of the effect.

| Key | Type | Required | Notes |
|---|---|---|---|
| `brightness` | number |  | Default 1. min 0 |
| `color` | [colour](#colour) |  | sRGB. Default white. |
| `fadeIn` | number |  | Seconds. min 0 |
| `fadeOut` | number |  | Seconds, at the end of the lifetime or after the effect stops. min 0 |
| `flicker` | number |  | min 0 max 1 |
| `lifetime` | number |  | Seconds. Default 0: until the effect is stopped. min 0 |
| `offset` | [vec3](#vec3) |  | Metres, in the effect's space. |
| `radius` | number |  | Metres. Default 6. Above 64 is clamped. |

## material

A surface on the game's own shaders. Everything has a default: a material with no maps at all is a flat colour.

| Key | Type | Required | Notes |
|---|---|---|---|
| `albedo` | [materialPng](#materialpng) |  | Colour, sRGB. Its alpha is what a cutout cuts by. |
| `alphaTest` | boolean or number |  | Cut where the albedo's alpha is under this. true is 0.5. The game's shaders cut at 0.5, so another value rescales the alpha. |
| `contactShadows` | boolean |  | Default true. |
| `dissolve` | boolean or object |  | The game can fade it in and out with an edge glow, as it does to creatures and placed objects. |
| `doubleSided` | boolean |  | Draw both faces, with the normal turned on the back. |
| `emissive` | [materialPng](#materialpng) or [colour](#colour) or object |  | What glows: a PNG, a colour, or both with a strength. |
| `frost` | boolean |  | Takes frost in the cold. Default true. |
| `kind` | `"opaque"` \| `"metal"` \| `"cutout"` \| `"foliage"` \| `"cloth"` \| `"emissive"` \| `"dithered"` |  | What it is, and where every other field starts from. Default opaque. metal: metallic 1, roughness 0.35. cutout: holes where alpha is under 0.5. foliage: cutout, double sided, light comes through. cloth: double sided, rough. emissive: glows, strength 4. dithered: soft edges from a dither pattern. |
| `metallic` | [unit](#unit) |  | The value without a map, a factor with one. Default by kind, 0 for opaque. |
| `metallicRoughness` | [materialPng](#materialpng) |  | glTF's layout, linear: G roughness, B metallic. |
| `normal` | [materialPng](#materialpng) or object |  | A tangent-space normal map, green up as in glTF and Blender. |
| `occlusion` | [materialPng](#materialpng) |  | Linear, in R. |
| `params` | [materialPng](#materialpng) |  | The game's own layout, linear: R metallic, G roughness, B occlusion, A reflectance if reflectance says "params". |
| `reflectance` | [unit](#unit) or `"params"` |  | Non-metal specular, a number. "params" reads it from the alpha of the params map. Default 0.503, what nearly all of the game's materials use. |
| `replaces` | string |  | The GUID of one of the game's materials: this block draws in its place on every model that uses it (items, props, creatures). Keep its kind: the models carry the original's cutout and double-sided flags. Client-side only; a mod that only replaces may be side client. |
| `roughness` | [unit](#unit) |  | The value without a map, a factor with one. Default by kind, 0.7 for opaque. |
| `shader` | string |  | Reserved for surface shaders, which aren't built yet: Kiln refuses a material that names one. min length 1 |
| `shadows` | boolean |  | Casts a shadow. Default true. |
| `snow` | boolean |  | Takes snow. Default true. |
| `tint` | [colour](#colour) |  | sRGB. Multiplies the albedo; without one it is the colour. Built into the texture: the game's block has no tint of its own. |
| `translucent` | [unit](#unit) |  | How much light comes through from behind. Default 0, 1 for kind foliage. |
| `uvScale` | number or array of number |  | Repeats of every map: a number, or [u, v]. Default 1. |
| `vertexColor` | boolean |  | Multiply the albedo by the model's vertex colour. |
| `was` | string |  | The block's old name: the id stays the old one, so a model that names it doesn't notice the rename. min length 1 |
| `wetness` | boolean |  | Darkens in the rain. Default true. |

## materialPng

Relative to your mod's folder. A power of two on both sides, 4 to 4096. Pattern `\.[pP][nN][gG]$`. A file that must exist.

Type: string

## materialSection

Surfaces for models, by name. Each becomes one material with an id from your mod id and its name. Name one in a model step of assets/cook.json.

Type: object

## name

The block's name. Ids come from your mod id and this: keep it. A rename says "was". Pattern `^[A-Za-z0-9_.-]+$`.

Type: string

## particle

A particle emitter. It needs a burst or a rate.

| Key | Type | Required | Notes |
|---|---|---|---|
| `acceleration` | [vec3](#vec3) |  | Metres per second squared in world space, added to gravity. |
| `align` | `"camera"` \| `"velocity"` |  | velocity: a camera-facing quad turns to point the way it moves (sparks, rain, streaks). Default camera. |
| `alpha` | [unitCurve](#unitcurve) |  | 0 to 1 over the particle's life. Default 1. |
| `blend` | `"alpha"` \| `"additive"` |  | Default alpha. |
| `burst` | integer |  | Particles at the start, and at each loop. min 0 max 65535 |
| `color` | [gradient](#gradient) |  | sRGB over the particle's life. Default white. |
| `delay` | number |  | Seconds before the first spawn. min 0 |
| `depthFade` | number |  | Metres of soft fade against the scene. min 0 |
| `drag` | number |  | Per second: velocity *= exp(-drag * dt). min 0 |
| `duration` | number |  | Seconds of emission. Without it a rate runs until the effect is stopped; a burst alone needs none. min 0 |
| `emissive` | number |  | How much the colour follows the camera's exposure correction. min 0 max 1 |
| `faceCamera` | boolean |  | Turn the mesh to the camera. Default: true for quad, false for others. |
| `flipbook` | object |  | The texture is a grid of frames. |
| `gravity` | number |  | Metres per second squared on world y: negative falls. |
| `intensity` | number |  | Multiplies the colour, which may then exceed 1. Default 1. min 0 |
| `lifetime` | [positiveValue](#positivevalue) |  | Seconds a particle lives: a number or [min, max]. Default 1. |
| `lit` | boolean |  | Take ambient and sun light through the fog volume. Default false. |
| `loop` | boolean |  | Start again after the duration, which must be above 0. |
| `material` | [name](#name) |  | A material of this mod: its albedo as the texture, its tint in the colour, and lit, as far as the particle says nothing itself. A sprite draws with the game's transparent shaders, so the rest of a material doesn't apply. |
| `maxCount` | integer |  | Particles alive at once. Derived when left out. The game reads it on the CPU, where the shaders can't enforce it, so it stops at 4096. min 1 max 4096 |
| `mesh` | `"quad"` \| `"quad-xz"` |  | quad faces the camera; quad-xz lies flat. Default quad. |
| `rate` | number |  | Particles per second. At most 64 spawn per tick. min 0 |
| `roll` | [value](#value) |  | Degrees at spawn: a number or [min, max]. |
| `sampler` | `"wrap"` \| `"mirror"` \| `"clamp"` \| `"clampToZero"` |  | How the texture repeats. Default wrap. |
| `shader` | string or object |  | Advanced: this mod's own particle module, a .spv built with kiln-vfxcook shader build (docs/kiln/modding/shaders.md). Needs "advanced": true in kiln.mod.json. If it can't be used, Kiln's own sprite draws the particle. |
| `shape` | `"point"` or object or object or object or object or object or object or object |  | Where particles start, in the effect's space (its scale and rotation apply). Sizes in metres. Default: the effect's position. |
| `size` | [curve](#curve) |  | Metres over the particle's life. Default 0.25. Above 64 is clamped. |
| `spawnRadius` | number |  | Metres: start inside a sphere this big. Default 0, at the effect's position. The same as shape {"sphere": r}: say one or the other. min 0 |
| `speed` | [value](#value) |  | Metres per second at spawn: a number or [min, max]. Default 1. |
| `spin` | [value](#value) |  | Degrees per second: a number or [min, max]. |
| `spread` | `"up"` \| `"sphere"` \| `"outward"` or object |  | Direction at spawn, around the effect's up. outward: away from the centre of the shape. Default up. |
| `stretch` | number |  | With align velocity: extra length in metres per metre per second of speed. min 0 max 100 |
| `texture` | [png](#png) or object |  | A PNG in your mod, power of two on both sides, 4 to 2048. Without one the particle is white. |

## png

Relative to your mod's folder. Pattern `\.[pP][nN][gG]$`. A file that must exist.

Type: string

## positiveRange

min items 2 max items 2

Type: array of number

## section

Effects by name. Each becomes one VFX with an id from your mod id and its name.

Type: object

## unit

min 0 max 1

Type: number

## unitCurve

A curve from 0 to 1.

Type: number or array of number or object or object

## vec3

min items 3 max items 3

Type: array of number

