# kiln.assets mod manifest (assets/assets.json), format 1

<!-- Generated from schema/assets.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Icons, retextures, resources and content blobs a mod serves (native/services/assets/assets.cpp load_manifest; nlohmann::json, comments allowed, no trailing commas). Paths are relative to the assets/ folder; absolute paths and '..' are refused. Wrong value types stop the whole manifest. Docs: docs/kiln/services/assets.md.

- Files: `**/assets/assets.json`
- JSON: comments allowed, trailing commas **not allowed**
- Schema: [`schema/assets.schema.json`](../../../../schema/assets.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `entries` | array of [entry](#entry) |  |  |
| `format` | `1` |  | 1 (not checked by Kiln today; keep it for later readers). |

## entry

| Key | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"icon"` \| `"texture"` \| `"resource"` \| `"blob"` | yes |  |

## file

a path relative to assets/ (subfolders allowed; no absolute path, no '..') min length 1 A file that must exist.

Type: string

## guid

A resource GUID in .NET Guid text form (the bytes of Guid.ToByteArray() = the game's resource-id order).

Type: string

## type

A qualified reflection name (keen::UiTextureResource), hashed FNV-1a-32, or "0x<hash>". min length 1

Type: string

