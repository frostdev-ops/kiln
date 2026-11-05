# Kiln server modpack manifest (kiln-modpack.json at the zip root), format 1

<!-- Generated from schema/kiln-modpack.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

A modpack is a deterministic zip: kiln-modpack.json plus one folder per mod at <kind>/<id>/ (kiln/<id>/, native/<id>/, emf/<id>/) or plugin/<file>.dll. The launcher checks the zip's sha256 (from the server's network.json / last-server.json), then this file, then each mod folder's kdh1 before it imports anything. Contains the mods players need (manifest clients: mod).

- Files: `**/kiln-modpack.json`
- JSON: comments **not allowed**, trailing commas **not allowed**
- Schema: [`schema/kiln-modpack.schema.json`](../../../../schema/kiln-modpack.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `created` | `common.schema.json time` |  |  |
| `format` | `1` | yes |  |
| `kiln` | `common.schema.json range` |  | Kiln versions players need (default: any). |
| `mods` | array of object | yes |  |
| `name` | string | yes | min length 1 max length 128 |
| `server` | object | yes |  |
| `version` | string | yes | Free-form (the server admin's modpack version). min length 1 max length 64 |

