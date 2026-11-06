# Data packs and data files

Wiki: [data packs](https://frostdev.io/kiln/wiki/data-packs), [new items](https://frostdev.io/kiln/wiki/new-items),
[game data](https://frostdev.io/kiln/wiki/game-data), [creatures](https://frostdev.io/kiln/wiki/creatures),
[assets](https://frostdev.io/kiln/wiki/assets). Prefer a pack over code whenever the change is data. `kiln lint` checks them.

## Where and when
`<mod>/content/*.json`. Applied when the world's registries have loaded (before the save is read), in mod load order,
then file-name order. A pack follows its mod: unload the mod and its pack comes off; edit a file and every pack is
re-applied (checked every ~2 s). Packs apply even when no mod references Kiln.Content.

## Format
```json
{
  "format": 1,
  "items": {
    "Material_T1_Wood_Logs": { "maxStackSize": 1000 },
    "*": { "@where": { "maxStackSize": ">1", "category": "Materials" },
           "maxStackSize": { "mul": 4, "max": 9999, "raiseOnly": true } },
    "Weapon_T1_1H_SwordEpic_01": { "damageSetup.dmgMod": 1.25, "rarity": "Epic" }
  },
  "recipes": {
    "Tool_basic_FishingRod_T0_PK": { "inputs": [ { "item": "Material_T1_Twigs", "count": 1 } ] },
    "Ammo_Arrow_T1_Flint": { "outputs": { "mul": 2 } },
    "*": { "@where": { "output": "Material_T1_Charcoal" }, "craftingDuration": 30 }
  },
  "loot":  { "Material_T1_Wood_Logs": { "stackSizeMin": 5, "stackSizeMax": 20 } },
  "drops": { "*": { "count": { "mul": 2 }, "chance": { "mul": 1.5, "max": 1 } } },
  "options": { "stackGuard": true }
}
```
- **Selectors**: a debug name, an id (`0x2b0cf1d5`), or a case-insensitive glob (`Material_*`, `*`).
- **`@where`**: conditions on the game's original values: equality, `">1"`, `">="`, `"<"`, `"<="`, `"!="`, a list
  = any of; `name` (glob); recipes also `output` / `input`.
- **Fields**: paths from the game's reflection. Get the real names from `kiln catalog search` or `Content.Dump()`
  (`kiln/data/content/dump.json`), never guess. Identity fields (ids, GUIDs, flags) are read-only.
- **Values**: numbers, bools, enum names, times in seconds, or ops on the original:
  `{"mul", "add", "min", "max", "raiseOnly", "lowerOnly"}`, `{"set": v}`, `{"reset": true}`.
- **Recipe lists**: `inputs` / `outputs` = the whole new list (1–32 `{"item", "count"}` or `{"category", "count"}`),
  or `{"mul": x}` to scale.
- **Conflicts**: later entries in a pack win; across packs the later mod wins; code changes win over packs. Conflicts
  and problems (unknown field, out of range, selector matching nothing) are logged per file and in
  `Content.PackReport()`; they never stop other entries.

## New items and recipes: `"add"`
```json
{ "format": 1,
  "items": { "add": [ {
      "guid": "7d3e9b52-1c84-4f0a-a6d7-5e2b8c9f1a03", "name": "MyMod_Glacier", "base": "Weapon_T1_1H_SwordEpic_01",
      "model": "item:Weapon_T4_1H_SwordEpic_02", "icon": "icons/glacier.png",
      "texts": { "en": { "name": "Glacier" }, "de": { "name": "Gletscher" } },
      "fields": { "damageSetup.dmgMod": 1.6, "rarity": "Epic" },
      "loot": { "like": "Material_T1_Metal_Scraps" },
      "drops": [ { "table": "like:Material_T1_Metal_Scraps", "countMin": 1, "countMax": 2, "chance": 0.25 } ] } ] },
  "recipes": { "add": [ {
      "guid": "84d38550-b3df-426d-a42d-5b1259156447", "name": "MyMod_Glacier_Recipe", "station": "hand",
      "inputs": [ { "item": "Material_T1_Twigs", "count": 2 } ], "outputs": [ { "item": "MyMod_Glacier", "count": 1 } ] } ] } }
```
- `guid` fixed forever (generate once). `name` unique debug name. `base` required: the clone's behaviour, stats, model
  and icon unless you change them.
- Kitbash references (`model`, `icon`, `perks`, `weaponType`, `visual`, `impact`, `refs`) point at things the game
  already loaded (`item:<name>` or a GUID) or that kiln.assets serves. Changing a game item's references needs
  `"@refs": true` on the rule.
- `station`: `"hand"`, a recipe whose station to use, or `"0x<workshop id>"` (traders are workshops too). `unlock`:
  `Always` (default) or `LikeBase`.
- Clients: new items need a stated `clients: "kiln"` (definitions are synced; PNG icons are not) or `"mod"`.
- Removing a mod: its items stay registered until restart; after a restart, saved stacks become placeholders named
  `[Missing: <mod>:<item>]` and come back when the mod does.

## Game data sections (same files)
`templates` (creatures, projectiles, explosions; paths start with a component type name), `arsenals` (enemy
attacks), `sequences` (cast timing, cost events), `buffs`, `perks`, `skills` (skill-tree nodes), and `items` paths
into config values (`"impactValues.ManaCost": 30`).
```json
{ "format": 1,
  "items": { "Ammo_T1_Spell_Fire_Ball_lvl10": { "impactValues.ManaCost": 30 } },
  "templates": {
    "Enemy_Vukah_*": { "@where": { "Faction.faction": "Vukah" },
                       "Enemy01BehaviorState.combatCommitmentSetup.view.range": { "mul": 1.5 } },
    "add": [ { "guid": "…", "name": "MyMod_Elite_Vukah", "base": "Enemy_Vukah_Melee",
               "fields": { "Level.dataStorage[0]": 20, "ScaleComponent.uniformScale": 1.4 } } ] },
  "perks": { "Perk_Damage_Ice": { "damageModifier.distribution.magicalIceDamage": { "mul": 2 } } },
  "skills": { "item:Ability_DoubleJump": { "costs": 1 } } }
```
- Template edits are global (every entity of the template). Per-creature changes belong to kiln.creatures.
- An enemy's sight is `Enemy01BehaviorState.combatCommitmentSetup.view.range` (by day; times `nightRangeFactor` at night, the Vukah's 0.6),
  its cone `.view.coneHalfAngle`, the distance it engages a seen target at once `.gain.inRange.range`. Not `aggroSetup`: its `viewRange`
  only sizes the candidate search (never below 64 m), `aggroRange` and `viewConeHalfAngle` are not read.
- When a change shows: template-only data at once; spawned values (level, scale) at the next spawn; configs at the
  next cast or buff; costs at the next payment. Vanilla clients may show vanilla tooltips; Kiln clients get synced
  values.
- Perk and buff clones (`"add"` in `perks` / `buffs`) get their own names per language.

## Creatures section
```json
{ "creatures": {
    "variants": [ { "id": "elite_vukah", "base": "Enemy_Vukah_Melee", "name": "Elite Vukah", "level": 20,
                    "scale": 1.4, "resistances": { "Fire": 0.5 }, "features": ["-FleeHome"],
                    "ai": { "combatCommitmentSetup.view.range": 30 } } ],
    "spawnRules": [ { "replace": "Enemy_Vukah_Melee", "with": "elite_vukah", "chance": 0.2, "night": true } ] } }
```

## Assets: `assets/assets.json`
```json
{ "format": 1, "entries": [
  { "kind": "icon", "guid": "<item GUID>", "file": "icons/my_item.png" },
  { "kind": "texture", "guid": "<texture resource GUID>", "type": "keen::UiTextureResource", "part": 0, "file": "retex/x.png" } ] }
```
Kinds: `icon`, `texture`, `resource`, `blob`. Paths relative to `assets/`, no `..`. Icons and textures are client
only (skipped on a server). Models: write `assets/cook.json` and ship your glTF; `assets/cooked/` is made on the
player's machine and never shipped.
