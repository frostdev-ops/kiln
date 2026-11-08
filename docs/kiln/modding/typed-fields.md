# Typed fields: the game's data as C# structures

Blocks are the way to make things ([blocks.md](blocks.md)). When you need to change what a block doesn't cover, or one of the
game's own entries, you go below them, to the game's data. A path in a string does that:
```csharp
sword.Set("damageSetup.dmgMod", 1.5);
```
A typed field does the same, and your editor completes every step:
```csharp
sword.Set(GameFields.Items.DamageSetup.DmgMod, 1.5f);
```
The field exists because your game has it. Its type comes from the game too: `1.5f` fits, `"1.5"` and the double `1.5` don't
compile.

## Turning it on
```xml
<KilnServices>Content</KilnServices>
<KilnGameIds>Items;Templates;Fields</KilnGameIds>
```
`Fields` makes `GameFields` and `GameEnums`. Like the game ids, it comes from the catalog `kiln catalog build` makes from your own game,
and nothing from the game is in Kiln.

It's off by default because of its size: all of it adds about 1.3 MB to the mod's dll and a few seconds to each compile, and nearly
all of that is templates. If you only change items, `Fields.Items` adds about 130 KB. Sections can be combined:
`<KilnGameIds>Items;Fields.Items;Fields.Perks</KilnGameIds>`.

## What you get
```csharp
using Kiln;
using Kiln.GameIds;

var sword = Content.Items.Get(GameItems.Weapon_T1_1H_SwordEpic_01)!;
sword.Set(GameFields.Items.DamageSetup.DmgMod, 1.5f);                // a number
sword.Set(GameFields.Items.MaxStackSize, 5);                         // 5 fits a ushort; 70000 doesn't compile
sword.Set(GameFields.Items.Rarity, GameEnums.ItemRarity.Legendary);  // an enum from the game
sword.Set(GameFields.Items.DisableRarityGeneration,                  // a flag set
          GameEnums.ItemRarityMask.Common | GameEnums.ItemRarityMask.Rare);

float now = sword.Get(GameFields.Items.DamageSetup.DmgMod);
float was = sword.Original(GameFields.Items.DamageSetup.DmgMod);
sword.Reset(GameFields.Items.DamageSetup.DmgMod);                    // your layer comes off
```
| In the game | In C# |
|---|---|
| `float32` | `float` |
| `uint8` ... `sint64`, ids like `ItemId` | `byte` ... `long`, `uint` |
| `bool` | `bool` |
| `Time` | `TimeSpan` |
| enum | a `GameEnums` enum |
| flag set | a `[Flags]` `GameEnums` enum |
| reference | `string`: a name or a GUID, so `GameTemplates.X` works |
| GUID | `Guid` |
| anything else | `JsonNode`, the value as a data pack writes it |

Identity fields (`itemId`, `guid`) and names can be read. Setting one doesn't compile, because Kiln refuses those changes.

**Templates** start with the component, as their paths do:
```csharp
var bolt = Content.Templates.Get(GameTemplates.Projectile_Player_Staff_IceBolt_1)!;
bolt.Set(GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(8));
bolt.Set(GameFields.Templates.StaticGravityModifier.Modifier, 0.5f);
```

**Config values** go by the names the game's Impact programs give them. The type comes from those programs:
```csharp
ammo.Set(GameFields.Items.ImpactValues.ManaCost, 20);
bolt.Set(GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate, GameTemplates.Explosion_Player_Spell_IceBolt_1);
burst.Set(GameFields.Templates.ImpactConfiguration.ConfigValues.PushBackStrength, GameEnums.PushBackForce.Big);
```
A config your own program declares isn't in the catalog: `ImpactValues.Named<float>("mymod.Radius")`, or by id with
`ById<float>(0xa19409b2)`.

**Lists:**
```csharp
GameFields.Items.Tags[2].Tag                          // one element
GameFields.Items.Tags.All.Tag                         // every element: a write changes them all
GameFields.Items.Tags.Self                            // the list as one JSON value
GameFields.Sequences.Events["SpawnEntityEvent#0"]     // by type in a list of variants, or by GUID
```
**Past what the catalog can see.** A list of variants holds different types, so the catalog stops there. `Below` goes on with a type
you choose:
```csharp
var time = GameFields.Sequences.Events["SpawnEntityEvent#0"].Below<TimeSpan>("time");
```

## Clones and new items
```csharp
Content.Templates.Define(new GameDataClone
{
    Guid = myGuid, Name = "My_Bolt", Base = GameTemplates.Projectile_Player_Staff_IceBolt_1,
    Values =
    {
        { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(5) },
        { GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate, "My_Burst" },
    },
});

Content.Items.Register(new ItemDefinition
{
    Guid = myItem, Name = "My_Sword", BaseOn = GameItems.Weapon_T1_1H_Sword_01,
    Fields = new FieldValues { { GameFields.Items.MaxStackSize, 1 }, { GameFields.Items.Rarity, GameEnums.ItemRarity.Epic } },
});
```
They give the same definition as the same fields in a data pack.

`field.Path` is the string a data pack writes, and it's what the field does underneath. There is one set of layers, so strings and
fields can be mixed.

## String paths are checked too
Strings keep working: `base` clones, string paths and data packs didn't change. With a catalog on the machine, the compiler checks
them:
```
error KILN194: 'damageSetup.dmgMood' is not a path of items: damageSetup has no field 'dmgMood'. Did you mean 'damageSetup.dmgMod'?
```
It checks constant strings, where the code says which section the entry is in: `Content.Templates.Get(...).Set("...")`, also through
locals. A `GameDataEntry` passed in as a parameter could be anything, so it isn't guessed. In the editor, a code fix writes the
suggestion. The rules: [analyzers.md](analyzers.md#kiln194). Data packs get the same check from `kiln lint`
([lint.md](lint.md)).

## Any resource
Items and templates have typed fields. The game keeps hundreds of other kinds of data as resources: buffs, perks, balancing tables,
recipes, sound containers. `ReflectedResources` reads any of them, with the types your game reports about itself (kiln.types).
No catalog is needed.
```csharp
var sword = ReflectedResources.Get(Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95"), "keen::ItemInfo")!;
Log.Info($"{sword["debugName"]} stacks to {sword["maxStackSize"]}");     // "Weapon_T1_1H_SwordEpic_01 stacks to 1"
sword["maxStackSize"] = 5;                                              // an override, undone when your mod unloads
```
**Paths** are the ones `Reflected.Component` takes: `"maxStackSize"`, `"damageSetup.dmgMod"`, `"tags[2]"`. They also go through the
parts the game stores behind the resource: lists (`"tags[2]"` on a list), optional parts (`"extra.power"`) and variants (`"part.x"`, the
fields of whatever struct the variant holds).
`Count("tags")`, `IsSet("extra")`, `VariantType("part")` and `Describe()` show what a resource holds. A read gives `bool`, `long`,
`ulong`, `double`, `string` or `Guid`.

**Find by a field's value.** `FindByField` looks at every resource of a type. The value is checked like a write (an enum by name,
text for text, a GUID or its text for a reference):
```csharp
foreach (var item in ReflectedResources.FindByField("keen::ItemInfo", "maxStackSize", 1)) { /* every item that does not stack */ }
var byName = ReflectedResources.FindByField("keen::ItemInfo", "debugName", "Weapon_T1_1H_SwordEpic_01").Single();
```
A resource that has no such element (an optional that is not set, a list that is too short) does not match. A path that is no field of
the type throws. `ReflectedResources.All(type)` lists the resources of a type. `Assets.Reflect(key)` opens one by `AssetKey`.

**Writes are claims.** A write keeps the resource's size: it changes numbers, enums, flags, bools, GUIDs and references, and it does
not make text or lists longer or shorter. The game gets the resource with your value in it (copy-on-write: its own bytes are never
touched). The value is checked first; one that does not fit throws and changes nothing.
```csharp
var result = sword.Set("maxStackSize", 5);          // a result, not an exception, when another mod has the field
if (!result.Ok) Log.Warn(result.Message);
sword.Restore("maxStackSize");                      // the original comes back; sword.RestoreAll() for the whole resource
```
- When your mod unloads, everything it changed is given back.
- Two mods can change **different fields** of one resource: the game gets both, and each mod takes out only its own.
- Two mods that want **the same field**: the first keeps it. The second gets `OverrideStatus.Conflict`, nothing changes, and its log has
  a warning that names both mods. The indexer (`sword["x"] = 1`) throws `InvalidOperationException` with the same message.
- `Overrides.Active` lists every claim in force. `sword.HeldBy("path")` says who has a field. `sword.Original("path")` reads what the
  game had before any change.
- The game uses a changed resource when it next loads it. Data it keeps resident is read at the start: change that in `OnLoad`
  ([assets.md](../services/assets.md)).

**Reversible changes of any kind.** `Overrides.Claim` gives the same rules to anything you can undo:
```csharp
var r = Overrides.Claim("mymod:spawn-rate", () => myRate = 2, () => myRate = 1);
if (!r.Ok) Log.Warn(r.Message);    // another mod claimed "mymod:spawn-rate"; apply did not run
Overrides.Release("mymod:spawn-rate");     // or Overrides.RestoreAll(); the unload does it too
```
Use one name for one thing, the same in every mod that may want it. `restore` runs when you release the claim or your mod unloads.

### Helpers from EML and ShroudForge
Small ports that EML and ShroudForge mods use, as C# (clean implementations, MIT):
| What | In Kiln |
|---|---|
| `game.guid.hash(guid)` | `GuidHash.Of(guid)` (Kiln.Eml). `GuidHash.IdOf(guid)` is the id the game makes of a GUID: 0 for the zero GUID. `ItemInfo.itemId` is `IdOf(its GUID)` |
| `game.types.of(value)` | `GameTypes.TypeOf(value)`: a reflected component, event copy or resource, a field, or a type |
| `game.assets.get_all_contents()` | `Assets.ListBlobs()`: the blobs your mod added or overrode (`ListAllBlobs()` for every mod). Blobs from `assets.json` and the game's own are not listed |
| `image.create`, `get_pixel`, `set_pixel`, `encode`, `decode` | `DataImage` (Kiln.Eml): RGBA8 pixels, `GetPixel`/`SetPixel`, packed `0xRRGGBBAA`, `DecodePng`/`EncodePng` |
```csharp
var img = new DataImage(256, 256);
for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++) img.SetPixel(x, y, (byte)x, (byte)y, 0x80, 0xFF);
Assets.RegisterIcon(itemGuid, img.EncodePng());     // an icon drawn in code (client)
```
`DataImage` reads every PNG (all colour types and bit depths, transparency, interlaced) and writes 8-bit RGBA. Other formats and the
game's block-compressed textures are not in it: `Assets.OverrideTexture` takes a PNG and encodes it to the texture's format.
Types by Impact name (`get_by_impact_name`) are not here: kiln.types does not report them yet.

## Limits
- **Below a variant, nothing is typed:** sequence events' own fields and collider shapes. Use `Below`, or a string.
- **A field is typed per section, not per entry.** `GameFields.Templates.Health` exists on every template, whether or not that one has
  the component. Writing it where it's missing is the same run-time error as with a string.
- **A field of another section** (an item's field on a template) is an error where the code shows both, and otherwise an exception when
  it runs.
- **Your mod compiles against your game's build.** A field the game later renames stops compiling after the next `kiln catalog build`.
  That's the point: it would have failed anyway, later.

Sample: `mods/samples/TypedFields`. `Refused/` next to it is a mod with a typo that doesn't build.
