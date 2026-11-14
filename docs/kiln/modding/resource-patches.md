# Resource patches: change a field of the game's data before the game reads it

Some of the game's data is read once, when the game starts: the balancing table (experience per level, Flame Altars
per flame level, the level cap) is the best-known case. A change made in `OnLoad` comes too late for it: Kiln serves
your changed copy, but the game already holds what it read.

A resource patch is made earlier. You state it in `Mod.OnBoot` (or in a blocks file), Kiln checks it before the game
starts, and writes it at the moment the game's resource service is up and nothing has read from it yet.

Needs a mod with `"boot": true` in `kiln.mod.json` (see [OnBoot](onboot.md)) and the capability
`mods.boot.resource_patches`.

## In a blocks file

```json
{
  "format": 1,
  "resourcePatches": {
    "more-altars": {
      "format": 1,
      "resource": "82706b40-61b1-4b8f-8b23-dcec6971bda1",
      "type": "keen::BalancingTable",
      "set": {
        "altarsPerFlameLevel[*]": { "min": 16 },
        "experienceNeedPerLevel[0]": 1500
      }
    }
  }
}
```

- `resource`: the resource's GUID. `type`: its qualified type name. `part`: the part index, 0 when left out.
  [Typed fields](typed-fields.md), "Any resource", shows how to list a type's resources and their members.
- `set`: member path to value.

## Paths

| Form | Means |
|---|---|
| `playerLevelCap` | a member |
| `a.b.c` | a member of a nested struct |
| `list[3]` | element 3 of a list that exists (counted from 0) |
| `list[*]` | every element of the list, however many this game build has |

A path must end in a number, a bool or an enum. Strings, references to other resources, whole structs and the length
of a list cannot be patched.

## Values

| Value | Result |
|---|---|
| `1500`, `0.5`, `true`, `"EnumName"` | the member is set to it |
| `{ "mul": 2 }` | the game's value times 2 |
| `{ "add": 5 }` | the game's value plus 5 |
| `{ "min": 16 }` | at least 16 |
| `{ "max": 100 }` | at most 100 |
| `{ "raiseOnly": true, … }` | never below the game's value |
| `{ "lowerOnly": true, … }` | never above the game's value |

The keys combine: the result is the game's value times `mul`, plus `add`, then held between `min` and `max`. Whole
numbers are rounded. A bool or an enum member takes a plain value only.

## In C#

```csharp
public override void OnBoot(BootContext boot)
{
    var definitions = BootDefinitions.For(boot);
    definitions.Define("more-altars", new ResourcePatchBlock
    {
        Resource = Guid.Parse("82706b40-61b1-4b8f-8b23-dcec6971bda1"),
        Type = "keen::BalancingTable",
        Set =
        {
            ["altarsPerFlameLevel[*]"] = ResourceValue.AtLeast(16),
            ["experienceNeedPerLevel[0]"] = 1500,
        },
    });
}
```

## What is checked, and when

Before the game starts (a mistake here keeps your mod's boot content out, or stops the start when your mod says
`"boot": "required"`):

- the type exists in this game build, the path names real members and ends in a scalar;
- the value fits the member (an operation on a bool or an enum is refused);
- no other mod sets the same field. Two mods on one field are not combined: the mod that comes later in the load
  order is refused, and the report names both. The same plain value from both is accepted.

When the write is made (the game's data is in memory only then):

- the resource exists in this game build;
- an index like `[25]` is inside the list. `[*]` on an empty list changes nothing and is not an error.

A patch that fails then is reported in `kiln.log` and in `state/boot.json` with your mod, the patch's key and the path.
The start goes on without that patch. If your mod is `"boot": "required"`, the start stops instead.

## Limits

- **Both sides need the mod.** The patch changes the data of the process your mod runs in. A player who joins your
  server without the mod keeps the game's own values on their side, so what their game shows and predicts can differ
  from what the server does. A mod that patches data both sides read should say `"clients": "mod"`.
- **`OnBoot`'s game-data view shows the game's own values**, not patched ones: patches are written after `OnBoot`.
- A patch lasts for the run of the game. It is not written to disk and not stored in a save.
- The game's own hard limits still hold. Example: the game never allows more than 16 Flame Altars, whatever the table
  says.
