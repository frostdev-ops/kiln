# Kiln release binding (kiln-release.json, an asset of a release), format 1

<!-- Generated from schema/kiln-release.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Says which mod, version and zip a release holds. 'kiln publish' writes it for the exact zip it uploads; the update check reads it to know what a release is before anything is downloaded. The reader (kiln_manage::release_binding) is strict: at most 256 KiB, plain JSON (no comments, no trailing commas, no BOM), no key given twice in any object. Keys not listed here are ignored. A mod id may appear in one record only: a file that names a mod twice is not trusted for that mod.

- Files: `**/kiln-release.json`
- JSON: comments **not allowed**, trailing commas **not allowed**
- Schema: [`schema/kiln-release.schema.json`](../../../../schema/kiln-release.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `format` | `1` | yes |  |
| `mods` | array of object | yes | min items 1 max items 64 |

