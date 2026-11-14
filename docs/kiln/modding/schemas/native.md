# Kiln native mod manifest (native.json), format 1

<!-- Generated from schema/native.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

One per native mod folder (library/native/<id>/<version>+<hash8>/, kiln/native/<id>/ on a server or as a drop-in). Describes DLL mods Kiln loads with LoadLibrary: plain DLL mods (no init) and Kiln-aware ones (init export, native/core/abi/kiln_native.h). Comments and trailing commas are allowed. Unknown keys are kept and warned about.

- Files: `**/native.json`, `!**/config/native.json`, `!**/state/native.json`, `!**/impact/natives.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/native.schema.json`](../../../../schema/native.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `after` | `common.schema.json ids` |  | Native mods to load before this one, when present (ordering only). |
| `authors` | string or array of string |  |  |
| `before` | `common.schema.json ids` |  | Native mods to load after this one, when present. |
| `builds` | array of `common.schema.json sha256` |  | Game exe sha256s the mod was made for. When given and the running exe is not listed, the mod still loads with a warning (status note 'untested build'). |
| `clients` | `common.schema.json clients` |  | Default from side: server -> none, client/both -> mod. |
| `description` | string |  |  |
| `dlls` | array | yes | DLLs in the folder (subfolders allowed), loaded in this order. min items 1 |
| `enabled` | boolean |  | Default state; <configDir>/native.json and the profile entry override it. Default `true`. |
| `format` | `1` |  | Default `1`. |
| `id` | `common.schema.json modId` | yes |  |
| `incompatible` | `common.schema.json incompatible` |  |  |
| `init` | string or null |  | Export of dlls[0] called as int init(const KilnNativeContext*) right after it loads. null: only loaded. Not allowed with phase dllmain. Default `null`. |
| `kiln` | `common.schema.json range` |  | Kiln versions this mod works with (checked like a 'kiln' dependency). |
| `license` | string |  |  |
| `name` | string |  |  |
| `phase` | `"early"` \| `"boot"` \| `"world"` \| `"dllmain"` |  | kiln_native.h: early (start of kiln_boot, also in safe idle), boot (after Kiln plugins, no hooks needed), world (first world step, needs hooks), dllmain (loaded by the proxy in DllMain; chain.ini [profile]). Default `"early"`. |
| `requires` | `common.schema.json ids` |  | Native mods that must be present and load first; else this one fails. |
| `side` | `common.schema.json side` |  | Where it loads. Default client (most DLL mods are client mods; a hosting client runs the world). Default `"client"`. |
| `updates` | object \| null |  | Where new versions of the mod are published, for the launcher's and `kiln updates check`'s update notice (a notice and a link: Kiln never downloads or installs a version by itself). A tagged union by provider; each provider takes only its own properties. Never a reason to refuse a mod: a value Kiln can't read is ignored with a warning (KL103), and a provider this Kiln does not know means "update checks unsupported". Docs: docs/kiln/modding/manifest.md, "updates". Kiln warns (not refused). |
| `url` | string |  |  |
| `version` | `common.schema.json semver` | yes |  |

