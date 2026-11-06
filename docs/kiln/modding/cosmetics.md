# Cosmetics

The game has five vanity slots: head, upper body, arms, lower body and feet. A piece in one is drawn in place of the armour in the same slot, which keeps its stats. A `CosmeticBlock` makes such a piece, and a `WornLook` says what it looks like: a model of your own, or a game item's look.

```csharp
Blocks.Define("frost-hood", new CosmeticBlock
{
    Name = "Frost Hood",
    Slot = CosmeticSlot.Head,
    Look = new WornLook
    {
        Model = "models/frost-hood.gltf",   // a morph target named Female is its shape on a female body
        Attach = WornAttach.Head,           // a plain mesh, with its origin on the head joint
        Fits = Fit.Hood,                    // hair changes to its hood cut
        Hides = [BodyPart.Hair],
    },
    Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Plant_Fibre] = 12 } },
});
```
```jsonc
{ "format": 1,
  "cosmetics": {
    "frost-hood": { "name": "Frost Hood", "slot": "head",
      "look": { "model": "models/frost-hood.gltf", "attach": "Head", "fits": "hood", "hides": [ "hair" ] },
      "recipe": { "needs": { "Material_T1_Plant_Fibre": 12 } } } } }
```
Sample: `mods/samples/CosmeticSample`: the hood in C# and in data, a cape in data, and a look like a game item's. Its models are made by `make_models.py`, with no game data, and it has no recipe. Set `<KilnServices>Blocks</KilnServices>` in the project.

## CosmeticBlock
| Field | What |
|---|---|
| `Name`, `Description`, `Was` | As every block. Ids follow from the mod id and the name |
| `Slot` | `Head`, `UpperBody`, `Arms`, `LowerBody`, `Feet` |
| `Look` | A `WornLook`: inline, from `Blocks.Define`, by name, or `"other-mod:name"` |
| `Dyes` | Takes the game's dyes. They change what a material has a tint mask for, so a game material. A material block has no mask yet |
| `Recipe`, `Loot`, `Tweak` | As item blocks ([item-blocks.md](item-blocks.md)) |

A piece has no armour, no stats and no tooltip rows. It is made at one level and one rarity, as its recipe shows.

## WornLook
| Field | What |
|---|---|
| `Model` | A `.glb` or `.gltf` in the mod's folder. Also `item:<name>`, or the GUID of a model your `assets/assets.json` serves |
| `Like` | A game item, or one of this mod, whose whole look this takes: models, what it hides, its fits. What else the look says wins |
| `Attach` | For a mesh with no skin: the slot it sits on (`WornAttach.Head`, `Chest`, `Pelvis`, `FootLeft`, `HandRight`, ...) or `j<index>` |
| `Female` | A second file for a female body, instead of a morph target |
| `Variants` | Whole models for what the character wears or is: `[Fit.Shield] = "models/coat-shield.glb"` |
| `Fits` | Flags this piece raises, so that the rest of the character changes to suit it |
| `Hides` | Body parts, or another slot's piece, not drawn while this is worn |
| `Lods`, `PixelSizes` | Coarser files of `Model`, and when each shows |
| `Materials` | A glTF material's name, or `"*"`, to a [material block](materials.md) or a game material's GUID. One it leaves out becomes a block of its own |
| `Icon` | A PNG in the mod's folder, or `item:<name>`. Default: the game draws the model |

`wornLooks` in `blocks/*.json` holds looks to name from several pieces. Armor takes one too: `ArmorBlock.Look`, and an armor set's slots ([item-blocks.md](item-blocks.md)).

## Hair and beards
Hair styles and beards are items of the game's kind, drawn on the head from the character's choice. Their look is a `WornLook`, cooked like a cosmetic's.
```csharp
var crop = Blocks.Define("short-crop", new HairBlock { Name = "Short Crop", Look = new WornLook { Model = "models/short-crop.gltf" } });
Blocks.Define("trim", new BeardBlock { Name = "Trim", Look = new WornLook { Like = "VC_Beard12_stubble" } });
Barber.Wear(player, crop);      // on the server: the character's choice, saved with the character
```
```jsonc
"hair": { "short-crop": { "name": "Short Crop", "look": { "model": "models/short-crop.gltf" } } },
"beards": { "trim": { "name": "Trim", "look": { "like": "VC_Beard12_stubble" } } }
```
- Weight the mesh to the head joint of `kiln skeleton player`; `Attach` and `Fits` don't apply. Morph targets: `Female` on both; on hair `Hat` and `Hood`, the cut the game raises under a hat or a hood; on a beard `Jaw01`..`Jaw16`, its shape on each of the game's jaw shapes, which the character's head raises. `Variants` take the same flags (`[Fit.Jaw03] = "models/trim-jaw3.glb"`).
- The barber's and the character screen's lists show a style of yours beside the game's, with the preview of the game style it starts from (its look's `Like`, else a plain one): Kiln adds it to each Kiln client's lists. `Barber.Wear(character, block)` puts one on from the server, and `Barber.Of(character)` reads what is worn.
- `clients: mod` for a look with files of the mod's own; one like a game item's needs `kiln`.

## Making the model
**Skinned.** Weight the mesh to the player's skeleton. Get it from your own game:
```
kiln skeleton player player.glb             # --body female, or --body none
```
It holds the skeleton, with joints named `j<index>` and `j17_Head` where a slot sits, and a body to model around. Import it in Blender, model, weight to its bones and export the whole scene: the cook leaves out the node named `reference_body`. The file is the game's data, so keep it out of your mod. Both bodies share the one skeleton.

**Rigid.** A hat, a mask, a buckle: a plain mesh with no skin and `Attach`. The file's origin is the joint and its axes are the character's: y up, the wearer faces +z, their left is +x. The cook puts every vertex on that joint. No armature is needed.

**Both bodies.** Give the mesh a morph target named `Female` (in Blender, a shape key). The game raises it on a female character, so one model is both. Or give `Female` a file of its own. Blend shapes are position deltas only: normals stay the base's.

**Other flags.** A morph target named after a flag the slot looks at changes with it: `Tight` on an upper body bends sleeves when tight gloves are worn. Targets of other names are left out, with a warning. The client applies at most four at once.

| Slot | Flags it looks at, for morph targets and `Variants` |
|---|---|
| Head, Feet | `Female` |
| UpperBody | `Female`, `Tight`, `ArmsTight01`, `Shield` (unless the look hides the shield) |
| Arms | `Female`, `Shield` (the same) |
| LowerBody | `Female`, `NoArmor`, `LegsBeltTight`, `LegsFeetTight`, `LegsFeetTight01` |

A variant for a flag its slot doesn't look at is refused, since it would never be drawn. The client draws the first entry whose flags are exactly the raised ones, so Kiln writes one entry for every set of flags that a variant covers: a female body in tight gloves still gets the female file.

**What a piece raises.** `Fits = Fit.Hood` or `Hat` changes hair; `NoArmor` on an upper body changes the legs; `Tight` and `LegsFeetTight` on gloves and boots change sleeves and legs. Hair and other pieces without a cut for it stay as they are.

**Hiding.** Nothing is hidden unless the look says so. A chest piece that covers the chest says `Hides = [BodyPart.Chest, BodyPart.Waist]`, or the body shows through. `ArmorLowerBody` and the other slots hide what is worn there: a long coat over trousers. The "hide helmet" option of the game skips the head slot as it always does.

## The cook
A look's files are cooked on the player's machine, for the player's skeleton (`kiln cook`, [models.md](models.md)). `kiln cook` finds the looks in `blocks/*.json` by itself, and a mod whose looks are all there needs no recipe: `kiln cook --check` says whether they need cooking, and the report is `assets/cooked/cook-report.looks.json`. With a modelcook recipe in `assets/cook.json`, that recipe cooks them too.

The cook doesn't read code. A look written only in code needs a step in `assets/cook.json`:
```json
{ "format": 1, "tool": "modelcook", "steps": [
  { "op": "worn", "model": "models/wander-cape.gltf", "attach": "Chest", "materials": { "*": "wander_wool" } } ] }
```
`model`, `lods` and `attach` say what the look says; paths are relative to the mod's folder. A step for a file a look also names wins. A look in code whose file a data file also names needs no step, as the sample's hood. Code can name a block of its own mod's data files, and the other way round ([blocks.md](blocks.md)).

The model's GUID follows from `<mod>:model:<path without the extension>`, and the block works out the same, so there's nothing to copy between them. The cooked files are `assets/cooked/<path with _ for />.bin`, `.blob` and `.model.json`.

The build copies a mod's `models/` folder with it, as `blocks/` and `assets/` (`KilnModFolders`, [mdk.md](mdk.md)).

## What players' games need
A look with a file or a PNG icon of the mod's own needs `clients: "mod"`: every player needs the model, and nothing can send it. A look like a game item's needs `kiln`. The build works it out from code and from data files, `kiln lint` checks a manifest that says less, and a block that asks for more than the manifest says is refused when it loads.

A player without the mod is refused when joining, as for any `mod` mod. If a server lets one in anyway, their game draws nothing in that slot of a player who wears the piece, and they see the armour under it or the body. The lookup that draws a slot misses and leaves it empty. A Kiln client with the mod but without a cook sees the look the block starts from, a vanity piece of the game's, and its log says to run the cook.

## When something is wrong
| When | What |
|---|---|
| Load | `block cosmetic x, look.model: "models/hat.glb" is not in the mod's folder`, a slot or fit that doesn't exist with the ones that do, a variant its slot never looks at, clients too low |
| Load, client | A file that isn't cooked, a file cooked on another skeleton or with another `attach`, a morph target the slot never raises |
| Cook | A weighted vertex past four joints, a mesh with no skin and no `attach`, joints not where the player's skeleton has them, a delta a 16-bit float can't hold |
| `kiln lint` | The schema, a look's missing file, `clients` too low |

## Checks
| Check | What it does | Needs |
|---|---|---|
| `python3 -m unittest discover -s tools/modelcook/tests -p test_worn.py` | Morph targets read, written and decoded again as the vertex shader reads them; attaching; steps from blocks | nothing |
| `python3 tools/modelcook/tests/game_worn_check.py morphs` | Every model of the game with blend shapes, exported to glTF and cooked: its deltas back byte for byte | the game |
| `python3 tools/modelcook/tests/game_worn_check.py items`, `table`, `attach` | Vanity items and their models through tools/kfc byte for byte; the blend shape table against the executable; the game's hats attached to the head | the game |
| `python3 tools/modelcook/tests/game_worn_check.py blender` | `kiln skeleton player` into Blender, a piece made, weighted and given a shape key there, exported by Blender and cooked | the game, Blender |
| `python3 tools/modelcook/tests/worn_render.py OUT.png MODEL.bin ...` | Cooked pieces on the game's body, male and female, at rest and posed, on the CPU | the game, Pillow |
| `mods/tests/cosmetics-test` on a dedicated server | Registration, ids across a restart, C# and JSON the same, mistakes, per-body entries, a piece in a vanity slot giving no armour | a server |

The lab test needs the sample cooked (`kiln cook mods/samples/CosmeticSample`) and its own models: build it into a folder, then `tools/modelcook/modelcook.py recipe <that folder>/assets/cook.json --update-manifest`. It checks armor with a look as well. Start the server twice: the second run checks ids and stored items.

## Manual checks
Nothing here has been drawn by a game client yet. Back up your saves (`tools/client/client.sh backup-saves`), deploy the sample with `tools/client/client.sh deploy --real --policy any --mod <the built sample>`, and load a local world. `kiln.log` should say `assets: ...cosmetic-sample: icons 0, textures 0, resources 2, blobs 2, errors 0` and `blocks: cosmetic-sample: 7 block(s) from blocks/*.json, 0 error(s)`, with no `[model_loader]` line.

1. Craft the Frost Hood (12 plant fibre, in hand) and put it in the head vanity slot over a helmet. **Pass:** a blue hood, open at the face, on the head; hair gone; the helmet's armour still in the stats. It turns with the head when you look around and run.
2. The same on a female character. **Pass:** the hood is a little narrower; nothing pokes through it.
3. The Wanderer's Cape in the upper body vanity slot. **Pass:** a rust-red sheet down the back from the shoulders, following the chest when you turn. Dye it: the colour changes or stays, and either is worth writing down.
4. The game's "hide helmet": the hood goes and the hair comes back.
5. A second player with the mod sees both pieces on you; one whose copy of the mod isn't cooked sees the game's dress helmet and adventurer's top instead.
6. Back away 50 m: the pieces don't flicker or vanish before the body does.

Note even when it passes: clipping at the neck or shoulders, and whether the hood's hair cut showed (the hair should be hidden). Afterwards quit and `tools/client/client.sh undeploy --real`.
