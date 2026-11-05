# Kiln schematic header (the JSON inside a .kschem file), version 1

<!-- Generated from schema/kschem-header.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

The UTF-8 JSON header of a .kschem file (KSCHEM\r\n, u32 version 1, u32 header length, header, u32 cell data length, raw DEFLATE cells). Read by managed/services/Kiln.Build/Schematic.cs (System.Text.Json JsonNode: plain JSON). Docs: docs/kiln/services/build.md#the-kschem-format-version-1.

- Files: `**/*.kschem`
- JSON: comments **not allowed**, trailing commas **not allowed** (inside a binary .kschem file)
- Schema: [`schema/kschem-header.schema.json`](../../../../schema/kschem-header.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `anchor` | array |  | The paste point, in cells from the low corner (default: bottom centre). min items 3 max items 3 |
| `author` | string |  |  |
| `cellEncoding` | string |  |  |
| `cellOrder` | `"x,y,z"` |  |  |
| `cellSize` | number |  | Default `0.5`. |
| `description` | string |  |  |
| `format` | `"kiln.schematic"` |  |  |
| `name` | string |  |  |
| `props` | array of object |  |  |
| `size` | array | yes | Cells x, y, z (at most 4 M cells in all). min items 3 max items 3 |
| `source` | object |  | Where it was captured (optional, informational). |
| `version` | `1` |  |  |

