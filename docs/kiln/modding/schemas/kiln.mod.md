# Kiln mod manifest (kiln.mod.json), v2

<!-- Generated from schema/kiln.mod.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Validated by Kiln.Host at load time and by Kiln.Sdk at build time (managed/Kiln.Host/Manifest.cs is the reference). Comments and trailing commas are allowed. Docs: docs/kiln/modding/manifest.md.

- Files: `**/kiln.mod.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/kiln.mod.schema.json`](../../../../schema/kiln.mod.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `advanced` | boolean |  | Opt into the advanced tier (raw hooks, symbols, coremod-style patches); exposed as ModInfo.Advanced. Default `false`. |
| `apiVersion` | string | yes | Kiln mod API the mod needs. A bare version ("0.2") means that version or any later one with the same major (the API is additive within a major, 0.x included); anything else is a range checked against Kiln.Mods.ApiVersion. |
| `author` | string |  | Shorthand for a single author. |
| `authors` | string or array of string |  |  |
| `clients` | `"none"` \| `"kiln"` \| `"mod"` |  | What a player's game needs to join a server running this mod: none (vanilla clients join), kiln (Kiln on the client; the server sends the mod's definitions), mod (the mod itself, delivered by the server's modpack). Default from side: server -> none, client and both -> mod. |
| `dependencies` | object or array of object |  | Mods (or "kiln", the core version) this mod needs. Required ones load first and must match; optional ones load first when present and must match when present. |
| `description` | string |  |  |
| `entryAssembly` | [dll](#dll) | yes | The mod's assembly, in the mod folder. |
| `entryType` | string |  | Full name of the Kiln.Mod subclass (default: the only public one). |
| `exports` | array of [dll](#dll) |  | Assemblies dependents compile against; at run time they get these copies (shared types and state). |
| `features` | array of string |  | Kiln features that must be available on this game build (Game.HasFeature), e.g. tick, log.mirror. |
| `id` | [id](#id) | yes | Unique mod id; also the data folder kiln/data/<id>. |
| `incompatible` | array of [id](#id) or object |  | This mod does not load when one of these is present (in the given range). |
| `license` | string |  | SPDX expression, e.g. MIT. |
| `loadAfter` | array of [id](#id) |  | Load after these mods when they are present (no requirement). |
| `loadBefore` | array of [id](#id) |  | Load before these mods when they are present (no requirement). |
| `name` | string |  | Display name (default: the id). |
| `side` | `"server"` \| `"client"` \| `"both"` |  | Where the mod loads; mods for the other side are skipped. Default `"server"`. |
| `tests` | boolean or object |  | Kiln.Testing: true = autoRun. |
| `url` | string |  |  |
| `version` | string | yes | Semantic version (MAJOR.MINOR.PATCH[-pre][+build]); 1 and 1.2 are read as 1.0.0 and 1.2.0. |

## dll

Pattern `^[^/\\:]+\.[dD][lL][lL]$`.

Type: string

## id

Pattern `^[a-z0-9][a-z0-9._-]{0,63}$`.

Type: string

## range

Version range: * \| 1.2 (= ^1.2) \| ^1.2 \| ~1.2 \| =1.2.3 \| 1.x \| >=1.0 <2 \| 1.0 - 2.0 \| [1.0,2.0) \| [1.0,) \| (,2.0] \| [1.2.3]; \|\| between alternatives.

Type: string

