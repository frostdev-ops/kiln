# Kiln publishing config (kiln.publish.json in the mod project), format 1

<!-- Generated from schema/kiln-publish.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

Where and how 'kiln publish' releases the mod. The file never holds a secret: the provider's token is read from the PC's credential store when a plan is applied. 'kiln publish --plan' reads this file, audits the packed zip and freezes both into a plan; a change of this file after planning needs a new plan. Unknown keys are refused, so a typo cannot silently change what is published.

- Files: `**/kiln.publish.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/kiln-publish.schema.json`](../../../../schema/kiln-publish.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `format` | `1` | yes |  |
| `github` | object | yes |  |
| `notes` | string \| null |  | Path of the release notes (UTF-8, at most 1 MiB), relative to this file. They become the text of the release. |
| `packageFiles` | array \| null |  | Files of the zip that need a declaration: shipped default configs and third-party DLLs. A declaration never allows what the artifact policy forbids (game data, cooked output, saves, keys, Kiln's or .NET's own DLLs). max items 256 |
| `provider` | `"github"` | yes | The provider. This Kiln publishes to GitHub releases. |

