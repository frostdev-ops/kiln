# Kiln talent trees (skills/*.json), format 1

<!-- Generated from schema/skills.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

A mod's additions to the game's skill tree ("gameTree", read natively at boot by native/services/skills on every side: nlohmann::json, comments allowed, no trailing commas) and its own talent trees ("trees", read by Kiln.Skills: System.Text.Json). The same file goes through both readers: comments are fine, trailing commas are not. Beyond the schema, kiln lint checks what Kiln.Skills' Validate refuses (a node id twice, requires of an unknown node, two nodes in one cell). Owner of the format: skilltree. Docs: docs/kiln/services/skills.md.

- Files: `**/skills/*.json`
- JSON: comments allowed, trailing commas **not allowed**
- Schema: [`schema/skills.schema.json`](../../../../schema/skills.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `format` | `1` |  |  |
| `gameTree` |  |  | {"nodes": [...]}, or the list of nodes itself. |
| `trees` | array of [tree](#tree) |  |  |

## attribute

an attribute: Strength, Constitution, Intelligence, Spirit, Dexterity, Endurance, or an attribute GUID

Type: `"Strength"` \| `"Constitution"` \| `"Intelligence"` \| `"Spirit"` \| `"Dexterity"` \| `"Endurance"` or [guid](#guid)

## configMap

{"0x<configId>" or a config GUID: number}; true and false count as 1 and 0.

Type: object

## effect

an effect is one of {attribute, amount}, {gameNode}, {buff, refresh}, {program, configs}, {ability}

Type: object

## gameNode

A node added to the game's own skill tree: a fixed GUID (its id is FNV-1a of the 16 bytes), a unique name, and where it hangs (from / to).

| Key | Type | Required | Notes |
|---|---|---|---|
| `bidirectional` | boolean |  |  |
| `class` | `"Warrior"` \| `"Mage"` \| `"Ranger"` \| `"Neutral"` \| `"None"` |  | The screen's colour; None is Neutral. |
| `configs` | [configMap](#configmap) |  |  |
| `cost` | integer |  | Skill points (per level for nodes with levels). min 0 max 1000 |
| `description` | string |  |  |
| `displayName` | string |  |  |
| `from` | [nodeRefs](#noderefs) |  |  |
| `guid` |  | yes |  |
| `icon` | [guid](#guid) |  | A UiTexture GUID ("iconFile" takes a PNG). Names a **resources** entry (checked against the catalog). |
| `iconFile` | string |  | Pattern `\.[pP][nN][gG]$`. A file that must exist. |
| `like` | [nodeRef](#noderef) |  | A node to copy (type, look, configs). |
| `maxLevel` | integer |  | 0: one unlock; 1..7 (the game keeps a level in 3 bits). min 0 max 7 |
| `name` | string | yes | min length 1 max length 96 |
| `near` | [nodeRef](#noderef) |  |  |
| `offset` | [pair](#pair) |  | [dx, dy] from "near" (or the first "from"). |
| `perLevel` | string |  |  |
| `position` | [pair](#pair) |  | [x, y] in the game's tree units. |
| `program` | [guid](#guid) |  | An ImpactProgram GUID. Names a **resources** entry (checked against the catalog). |
| `scales` | [configMap](#configmap) |  |  |
| `texts` | [nodeTexts](#nodetexts) |  |  |
| `to` | [nodeRefs](#noderefs) |  |  |
| `type` | `"PassiveSkill"` \| `"Passive"` \| `"Attribute"` \| `"ActiveSkill"` \| `"Active"` |  | "type" must be PassiveSkill, Attribute or ActiveSkill (new roots are not allowed) |
| `unlocksAbility` | [guid](#guid) |  | The ability item's GUID (its ItemId is FNV-1a of it). Names a **items** entry (checked against the catalog). |

## guid

a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)

Type: string

## hexId

Pattern `^0[xX][0-9a-fA-F]{1,8}$`.

Type: string

## nodeRef

A game node "0x<id>", a node GUID, or a Kiln node's name. min length 1

Type: string

## nodeRefs

One node or a list of nodes.

Type: [nodeRef](#noderef) or array of [nodeRef](#noderef)

## pair

[a, b] or {"x": a, "y": b}.

Type: array or object

## programConfigs

A running program's config values, by name (any case) or id: numbers, booleans, GUIDs or other strings.

Type: object

## text

A text: a string (every language) or {"<language>": "text"}.

Type: string or object

## tree

A Kiln talent tree (Kiln.Skills): its id is the save key (convention <mod>:<name>), unique across mods.

| Key | Type | Required | Notes |
|---|---|---|---|
| `description` | [text](#text) |  |  |
| `icon` | string |  | A file that must exist. |
| `id` | string | yes | Pattern `^[A-Za-z0-9._:-]+$`. |
| `key` | string |  |  |
| `menu` | boolean or [text](#text) |  |  |
| `name` | [text](#text) |  |  |
| `nodes` | array of [treeNode](#treenode) |  |  |
| `points` | object |  | points: {start, perLevel} (its own points) or {share} (a share of the game's) |
| `respec` | boolean |  | Default `true`. |

## treeNode

| Key | Type | Required | Notes |
|---|---|---|---|
| `col` | integer |  | min 0 max 31 |
| `cost` | integer |  | Default `1`. min 0 |
| `description` | [text](#text) |  |  |
| `effects` | array of [effect](#effect) |  |  |
| `icon` | string |  | A file that must exist. |
| `id` | string | yes | min length 1 |
| `level` | integer |  | min 0 |
| `maxLevel` | integer |  | Default `1`. min 1 max 99 |
| `name` | [text](#text) |  |  |
| `pointsSpent` | integer |  | min 0 |
| `position` | array |  | min items 2 max items 2 |
| `requireAll` | boolean |  | Default `false`. |
| `requires` | string or array of string |  | Ids of nodes of the same tree. |
| `row` | integer |  | min 0 max 31 |

