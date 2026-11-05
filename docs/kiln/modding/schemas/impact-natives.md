# Kiln natives a mod's Impact programs call (impact/natives.json), format 1

<!-- Generated from schema/impact-natives.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Declares the mod's Kiln natives ("<modid>.<Name>") for tools/impact (the cook's argc check) and kiln.abilities (the verifier). Read with Python's json module (tools/impact/impact/catalog.py): plain JSON, no comments, no trailing commas. Docs: docs/kiln/services/abilities.md#impact-program-and-native-contract-runtime-side, docs/kiln/modding/impact.md.

- Files: `**/impact/natives.json`
- JSON: comments **not allowed**, trailing commas **not allowed**
- Schema: [`schema/impact-natives.schema.json`](../../../../schema/impact-natives.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `format` | `1` |  | Default `1`. |
| `natives` | array of object |  |  |

