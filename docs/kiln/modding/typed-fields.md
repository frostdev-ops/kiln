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

## Limits
- **Below a variant, nothing is typed:** sequence events' own fields and collider shapes. Use `Below`, or a string.
- **A field is typed per section, not per entry.** `GameFields.Templates.Health` exists on every template, whether or not that one has
  the component. Writing it where it's missing is the same run-time error as with a string.
- **A field of another section** (an item's field on a template) is an error where the code shows both, and otherwise an exception when
  it runs.
- **Your mod compiles against your game's build.** A field the game later renames stops compiling after the next `kiln catalog build`.
  That's the point: it would have failed anyway, later.

Sample: `mods/samples/TypedFields`. `Refused/` next to it is a mod with a typo that doesn't build.
