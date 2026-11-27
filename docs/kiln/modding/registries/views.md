# Registry views: one typed view over every registry and resource type

Since 0.2.0-dev.32 (experimental, `KILNX031`). One read-only view over everything Kiln can name in the game's data:

- **stable views** are the registries (`items`, `recipes`, `loot`, `drops`, `balance` and every registry a later schema
  adds): their fields read what the registry resolved, mapped and proven per game build as the registry is. They are written
  as before, through `Mod.OnRegistry`, `Registries.Change` and a data pack's `registry` section.
- **advanced views** are every other resource type of the running build: quests, NPC dialogs, materials, UI data, the long
  tail no registry covers yet. Their names are Kiln's, derived from the game's reflection, and the view only reads them
  (tier Advanced, [tiers.md](../tiers.md): verified per game build, not lab-proven).

Mods never name a game type, member, offset or address here: views, ids and fields are Kiln names.

## C#

```csharp
using Kiln.Registry;

if (RegistryViews.Available && RegistryViews.Find("quest") is { } quests)
{
    foreach (var id in quests.Select("game:quest_*"))
    {
        var quest = quests[id];
        if (quest.TryGet<int>("xpReward", out var xp, out var why)) Log.Info($"{id.Value}: {xp} xp");
        else Log.Info(why);                                         // this entry has no value of it, in Kiln words
    }
    var reward = quests["game:quest_main"].Get<RegistryId>("rewardItems[0].item");   // another view's entry
}
var maxStack = RegistryViews.Find("items")!["game:material_t1_wood_logs"].Get<int>("maxStack");   // the registry's value
```

| Member | What |
|---|---|
| `RegistryViews.All`, `Find(name)` | every view, stable ones first; one by its name |
| `RegistryView.Tier` | `Stable` (a registry) or `Advanced` |
| `RegistryView.Fields`, `Field(path)` | the fields on this build: Kiln path, type, element type, values, the view an id names (`Ref`), `ReadOnly`, `State` (`Readable`, `Unread`, `Removed`; a registry field `Mapped`, `Unmapped`, `Unproven`), `Reason`, and `CoveredBy` (`"items.maxStack"`: the registry field over the same member; of several, the one that maps it one to one, else the one whose own member it is, never a formula that only reads it), `Exact` when that field holds the same number or true/false (not an enum's own names, a formula or another conversion) |
| `RegistryView.Select(selector)` | ids by an id, a case-insensitive glob over ids, or an entry's debug name (a stable view reads it as a registry edit does: `Material_T1_Wood_Logs` is `game:material_t1_wood_logs`, `Material_*` `game:material_*`) |
| `view[id].Get<T>(field)`, `TryGet<T>(field, out value, out reason)` | one field, typed (`int`, `long`, `double`, `bool`, `string`, `string[]`, `RegistryId`, `TimeSpan`, `JsonNode`) |
| `view.TryRead(id, field, out JsonNode? value, out string? reason)` | one field as JSON |
| `view[id].Snapshot()` | every field of one entry, and per field why the others did not read |

`Get` throws `KilnUnavailableException` (with the reason) when the field does not read for this entry and
`KeyNotFoundException` when the view has no such entry or field on this build; `TryGet` and `TryRead` return false with the
reason instead. An id that names no entry reads as `null`: `null` into `RegistryId?`, `string` or `JsonNode`, and
`InvalidCastException` (`TryGet`: false) into `RegistryId` or another value type. Any thread. Nothing is cached in the C# objects: each read asks Kiln, so it follows the game's data (a
data pack's or a registry's change shows in the next read).

**First calls are slow; make them off the game's thread** (`Task.Run`, a load screen), never in a tick. Measured on the lab's
server build:
- a stable view's first read is the registry's first resolved read, which prepares the registry's values: 4 to 11 s
  (`Registries.Read` costs the same); later reads take milliseconds;
- an advanced view's first `Select`, read or `Snapshot` lists its entries, reading each one's name: about 0.2 s for
  `itemInfo` (4,268 entries) or `template` (12,269); Kiln keeps the listing until the type's resources change;
- a `Snapshot` decodes every field, every element of every list: up to about 1.2 s for one resource with long lists
  (`gameKnowledge`, `recipeRegistry`); read the fields you need instead.

A call holds back no other: other views, the registries and the catalog answer meanwhile; only a call that needs the same
view's listing waits for it.

Writing a registry by name, from code that does not know the registry's typed property (a registry a later schema adds):
`RegistryStage.Entries("creatures")["game:enemy_vukah_melee"].Set("health.perPlayer", 1.2)` inside `OnRegistry` or
`Registries.Change`. The generic `RegistryEntry` makes the same edits as the generated entry types and checks the value against
the field's schema type first.

## Names

| What | Stable view | Advanced view |
|---|---|---|
| view | the registry's name | the type's name without its namespaces and a `Resource` suffix, lower camel case (`npcDialog`); a name a registry or an earlier type holds: with its namespaces (`ecsTemplate`), then `<name>_<8 hex>` |
| entry ids | the registry's ([README.md](README.md#ids)) | the same rule: `game:<debug name in lower case>`, clash suffixes `~<8 hex>`; an entry without a name `game:<view in lower case>_<first 8 hex of its GUID>` |
| fields | the schema's | each member's name in lower camel case, dotted through structs (`setup.radius`); a list is a field of its own and its elements' fields are `<list>[].<field>`; read one with an index (`rewardItems[2].count`) |
| values | the schema's | numbers, `true`/`false`, an enum's or flags' values in lower camel case, ids, text, lists |

The advanced names start as the game's on the first build Kiln maps and are Kiln's from then on: when a game update renames
a type or a member, Kiln's alias table (`regmap/views.json`, Kiln's part like the rest of `regmap/`) keeps the Kiln name on the
renamed member, and a member a build removes keeps its name refused (never given to another member). The table holds deltas
only (identifiers, never a list of the game's names); with no row, a name is the derived one.

## Refusals

Per entry and per field, in Kiln words, each distinct one logged once in kiln.log (`registry view: ...`, at most 256 lines a
start):

| Case | Code (native) | Reason starts |
|---|---|---|
| before the registry stage ran (the game's resources are not loaded yet) | `KILN_ERR_UNAVAILABLE` | `the game's resources are not loaded yet` |
| no such view, entry or field on this build | `KILN_ERR_NOT_FOUND` | `there is no view ...`, `quest has no entry ...`, `quest has no field ...` |
| a member of a kind the view does not read in this release (a variant, an optional, a configuration list, a list of lists, deeper than 8 levels) | `KILN_ERR_UNAVAILABLE` | `quest.events: a member that holds one of several kinds of value ...` |
| this entry has no value of the field | `KILN_ERR_UNAVAILABLE` | `quest[game:quest_side].bonus: this entry has no value of it on this game build` |
| an enum value or flag Kiln has no name for | `KILN_ERR_UNAVAILABLE` | `quest[...].kind: the game holds a value Kiln has no name for on this build` |
| a member the alias table says a build removed | `KILN_ERR_UNAVAILABLE` | `quest.repeatable was removed by game build ...` |
| a stable field not mapped or not proven on this build | `KILN_ERR_UNAVAILABLE` | the registry's reason |

## Native: kiln.registry.view v1

`native/core/abi/services/kiln_registry_view.h` (48 bytes, served by kiln-content.dll on both sides): `views_json`,
`fields_json(view)`, `read(view, id, field)`, `select(view, selector)`, `entry_json(view, id)`. On a refusal the call returns
its code and copies the reason into the buffer the same way it copies an answer. Any thread; an advanced read decodes the
resident resource bytes under kiln.content's store lock, so it never sees a half-written value (and waits for a data-pack
application in progress). The catalog is built once per process at the first call after the stage; an advanced view's
entries are listed at the first call that needs them (about 0.2 s for thousands of entries, above) and again when its type's
resources change, and a call keeps the listing it started on.

## Not in this release

- Writing an advanced view. Write through a registry field where one covers the member (`CoveredBy`), or through the old
  path (a data pack section, a resource patch) where none does; a member that mods need to write becomes a registry field.
- Members that hold one of several kinds of value (template components, sequence events), optional members and the game's
  configuration lists: listed as `unread` fields with that reason.
- Lab-proven read times for advanced fields (they are read as the game holds them now; when the game reads them is not
  traced).
