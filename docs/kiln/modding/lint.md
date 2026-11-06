# kiln lint

`kiln lint` checks the files a mod writes before the game has to: each file against its schema, in the JSON dialect its
real reader accepts, plus the checks a schema cannot make (ids across mods, references to the game's data, the manifest
against the mod's content, cook recipes, licences). Every finding has a stable code (`KL103`) and rule name
(`unknown-key`), an exact position, and often a fix. The VS Code extension shows the same findings in the editor
([vscode.md](vscode.md)).

Field-by-field reference of each format, generated from the schemas: [schemas/](schemas/).

## Usage
```sh
kiln lint                                       # the current folder
kiln lint mods/MyMod                            # one mod
kiln lint mods/MyMod/content/items.json         # one file: checked with the rest of its mod, its own findings only
kiln lint --json mods/MyMod                     # the machine form (below)
kiln lint --catalog ~/catalog/client.json mods/MyMod
kiln lint --profile <launcher data>/profiles/pvp mods/MyMod
kiln lint --min-severity warning --off KL601 mods/MyMod
echo '{"format": 1, "items": {"Nope": {}}}' | kiln lint --stdin --stdin-path content/x.json
kiln lint --list-rules
```

| Option | What |
|---|---|
| `PATH...` | Files and folders (default: the current folder) |
| `--json` | Print the report as JSON instead of text |
| `--catalog F` | The game-data catalog: a file, or a folder holding `client.json` / `server.json` (see [The game-data catalog](#the-game-data-catalog)) |
| `--profile DIR` | Other mods to check against, whose own findings are not reported: a launcher profile folder (`profiles/<id>`, with `meta.json`), a folder of mods, or one mod |
| `--min-severity L` | Drop findings below `error`, `warning`, `info` or `hint` |
| `--off RULE` | Turn off a rule by code or name (repeatable): `--off KL601`, `--off no-license` |
| `--stdin --stdin-path P` | Lint text from stdin as if it were the file `P`, with the rest of its mod as on disk (an unsaved editor buffer). `P` need not exist |
| `--list-rules` | Every rule: code, default severity, name, title |

The exit code is 0 when there are no errors, 1 when there are, and 2 for a usage error. Warnings, infos and hints do not
fail a run.

### What gets read
- A folder with `kiln.mod.json`, `native.json` or a Kiln.Sdk project is a mod. Any other folder is searched for mods, up
  to 8 levels down, skipping `bin`, `obj`, `build`, `node_modules`, `.git`, `target`, `.out`, `.vs` and `.idea`.
- In a mod: `kiln.mod.json`, `native.json`, `content/*.json`, `skills/*.json`, `quests/*.json`, `blocks/*.json`, `assets/assets.json`, `assets/cook.json`,
  `impact/cook.json`, `impact/natives.json`, `kiln-modpack.json` and every `*.kschem`.
- Outside a mod: `kiln-modpack.json` and `*.kschem` are linted on their own.
- A Kiln.Sdk project (`<Project Sdk="Kiln.Sdk/...">`) generates its manifest at build time, so kiln lint reads the newest
  `obj/**/kiln.mod.json` and reports its findings on the `.csproj`. Fix those with the project's properties
  ([mdk.md](mdk.md)). Not built yet: [KL904](#kl904-manifest-not-built).

## Output

Text, one line per finding, then a summary (a problem matcher can read it):
```text
mods/MyMod/kiln.mod.json:8:3: warning KL103: "dependecies": unknown key (Kiln.Host ignores it and logs a warning); did you mean "dependencies"?
kiln lint: 0 error(s), 1 warning(s), 0 info(s) in 1 file(s); no game-data catalog (kiln catalog build)
```

JSON (`--json`), the contract tools build on: `schema/lint-report.schema.json`, with a full example in
`schema/fixtures/lint/output.json`.

| Report field | What |
|---|---|
| `format`, `tool`, `version` | `1`, `"kiln-lint"`, the Kiln version |
| `ok` | No errors |
| `checked` | The files read |
| `summary` | `errors`, `warnings`, `infos`, `hints` |
| `catalog` | `{path, side, build}`, or `null` without a catalog |
| `diagnostics` | The findings, sorted by file and position |

One finding, for the file `{ "id": "my-mod", "dependecies": {} }`:
```json
{
  "file": "/home/me/MyMod/kiln.mod.json",
  "code": "KL103",
  "severity": "warning",
  "rule": "unknown-key",
  "message": "\"dependecies\": unknown key (Kiln.Host ignores it and logs a warning); did you mean \"dependencies\"?",
  "span": { "start": 18, "end": 31 },
  "line": 1, "col": 19, "endLine": 1, "endCol": 32,
  "pointer": "/dependecies",
  "fixes": [ { "title": "Rename to \"dependencies\"",
               "edits": [ { "file": "/home/me/MyMod/kiln.mod.json", "span": { "start": 18, "end": 31 }, "newText": "\"dependencies\"" } ] } ],
  "related": [],
  "docs": "docs/kiln/modding/lint.md#kl103-unknown-key"
}
```
- `span`: UTF-8 byte offsets into the file, `end` exclusive. A key's span includes its quotes.
- `line`, `col`, `endLine`, `endCol`: 1-based, with columns in Unicode code points.
- `pointer` (optional): the JSON pointer of the value.
- `schema` (optional): the schema that reported it.
- `fixes`: text edits that resolve the finding; apply one fix's edits together.
- `related`: other places that matter (`{file, span, message}`), such as the first definition of a duplicate.
- `docs`: this page's rule section, or the format's own page for schema findings.

## JSON dialects
Kiln's readers do not agree on what JSON is, and a file your editor accepts can fail in the game. kiln lint holds each
file to its real reader. `schema/schemas.json` is the table; the test suite checks it against the readers themselves
(Kiln.Host's fixtures, Python's `json`, nlohmann::json).

| File | Comments | Trailing commas | BOM | Reader |
|---|---|---|---|---|
| `kiln.mod.json` | yes | yes | yes | Kiln.Host (System.Text.Json) |
| `native.json` | yes | yes | yes | kiln-core and the launcher |
| `content/*.json` | yes | no | yes | kiln.content (nlohmann::json) |
| `assets/assets.json` | yes | no | yes | kiln.assets (nlohmann::json) |
| `skills/*.json` | yes | no | yes | kiln.skills (nlohmann::json) and Kiln.Skills (System.Text.Json) |
| `quests/*.json` | yes | yes | yes | Kiln.Quests (System.Text.Json) |
| `blocks/*.json` | yes | yes | yes | Kiln.Blocks (System.Text.Json) |
| `assets/cook.json`, `impact/cook.json` | no | no | no | the cook tools (Python `json`) |
| `impact/natives.json` | no | no | no | tools/impact (Python `json`) |
| `kiln-modpack.json` | no | no | no | the launcher (serde_json) |
| `*.kschem` header | no | no | no | Kiln.Build (System.Text.Json) |

Formats without comments still take a `"comment"` key where the schema lists one.

## The game-data catalog
Checks against the game's items, templates, fields and languages need a catalog made from your installed game
(`kiln catalog build`, [catalogs.md](catalogs.md)). kiln lint looks, in order, at:
1. `--catalog` (a file, or a folder: `client.json`, then `server.json`);
2. the environment variable `KILN_CATALOG`, the same way;
3. `<launcher data>/catalog/`.

A path you named that holds no usable catalog is [KL902](#kl902-catalog-unreadable). Without a catalog those checks are
skipped, and [KL901](#kl901-no-catalog) says how many references went unchecked. Checks that need no game data still run,
including references to what only mods define (variants, natives, Kiln skill-tree nodes, talent trees).

## What kiln lint assumes
Some things cannot be known from the files alone, so kiln lint errs on the side of silence:
- **Names your code registers.** A name no pack defines is not reported as unknown when a linted mod's code mentions it:
  as a quoted string in a `.cs` file, or as a UTF-16 string in one of its DLLs (under 32 MB). Names under 4 characters
  never count.
- **Sections your code registers.** A data-pack section is not unknown ([KL103](#kl103-unknown-key)) when a mod's code
  mentions its name, as with `Content.RegisterSection("mysection", ...)`.
- **kiln.gamedata paths.** Field paths through `impactValues`, `configValues`, `uiValues`, `sequences` and
  `ImpactConfiguration` are accepted without checking what follows the first segment: config aliases and `0x` ids resolve
  at run time.
- **Case in file names.** Paths match without regard to case, because the game runs on Windows.
- **Source folders.** A mod folder with a `.csproj` builds its DLLs, so [KL205](#kl205-entry-missing) is skipped there.

A clean run is not proof that a mod works: it says the files are what their readers expect, not that the game does what
you meant.

## Schema annotations
Kiln's schemas (`schema/*.schema.json`, draft 2020-12) are ordinary JSON Schema that any validator can use, with these
extra keywords for kiln lint and the editor tools:

| Keyword | Meaning |
|---|---|
| `x-kiln-ref` | The string names an entry of this catalog section (or a mod-defined namespace). On `propertyNames`, the key does |
| `x-kiln-ref-forms` | The forms the reference may take: `name`, `id`, `guid`, `glob`, `item:`, `like:`, `template:`, `buff_`, `skill_`, `guid#k`, `lang`, `displayName`. Editors use them for completion. The linter recognises a form by the value's shape; only `lang` changes how it reads the value |
| `x-kiln-ref-missing` | Severity when the entry does not exist (default `warning`) |
| `x-kiln-def` | The string is a GUID this file defines, in this namespace |
| `x-kiln-def-name` | The string is a name this file defines, in this namespace |
| `x-kiln-file` | The string is a path that must exist, relative to `mod`, `assets` or `self` (the file's own folder) |
| `x-kiln-file-kind` | What the file must be (`png`) |
| `x-kiln-fields` | The object's keys are field paths of this catalog section (the game's reflection) |
| `x-kiln-unknownKeys` | Severity of a key the schema does not list. Without it, or with `additionalProperties` allowing the key, such keys pass silently |
| `x-kiln-unknownKeysMessage` | What to say about such a key |
| `x-kiln-severity` | Caps the severity of findings under this schema |
| `x-kiln-message` | One clear message instead of the raw failures at this location |
| `x-kiln-docs` | The page findings under this schema link to |
| `x-kiln-caseInsensitive` | On an enum: another case is a hint ([KL112](#kl112-case)), because Kiln ignores case there |
| `x-kiln-cook-base` | Documentation only: the input type a step's `base` must have ([KL510](#kl510-cook-base) checks it) |

Generated from the schemas, so do not edit them by hand: `schema/idrefs.json` (where files name game entries; the
extension's id completion reads it) and `docs/kiln/modding/schemas/*.md`. To regenerate after a schema change, run
`KILN_LINT_WRITE=1 cargo test -p kiln-manage --test lint` and review the diff. That also rewrites the expected findings
in `schema/fixtures/lint/`, which are the linter's contract.

## Rules
Severities are the defaults. Each section gives what the rule means, an example, and the fix.

## KL0xx: reading the file

### KL001 syntax
Error. The file is not valid JSON, so its other checks do not run.
```text
content/x.json:3:5: error KL001: strings use double quotes " in JSON
```
Fix: correct the JSON at that position.

### KL002 comment-not-allowed
Error. A comment in a file whose reader takes plain JSON ([dialects](#json-dialects)).
```text
assets/cook.json:4:3: error KL002: comments are not allowed: the cook tools read this file with Python's json module (plain JSON)
```
Fix: remove the comment (quick fix), or use a `"comment"` key where the format has one.

### KL003 trailing-comma
Error. A comma before `}` or `]` that the reader refuses.
```text
content/x.json:7:21: error KL003: trailing comma: kiln.content / kiln.assets read this file with nlohmann::json, which allows comments but not trailing commas
```
Fix: remove the comma (quick fix).

### KL004 duplicate-key
Warning. One object gives a key twice.
```json
{ "maxStackSize": 10, "maxStackSize": 20 }
```
```text
duplicate key "maxStackSize": the later value is the one Kiln reads
```
Fix: delete the one you did not mean.

### KL005 bom
Error. A UTF-8 byte-order mark in a file whose reader refuses it (Windows editors add one).
```text
assets/cook.json:1:1: error KL005: a UTF-8 byte-order mark: this file's reader (Python's json) refuses it
```
Fix: remove it (quick fix), or save the file as "UTF-8" without BOM.

### KL006 not-utf8
Error. The file holds bytes that are not UTF-8, for example a file saved as Latin-1.
```text
content/x.json:5:20: error KL006: not valid UTF-8 (Kiln's readers take UTF-8)
```
Fix: save the file as UTF-8.

### KL007 unreadable
Error. A file or folder cannot be read, or a path you passed does not exist.
```text
mods/Typo:1:1: error KL007: no such file or folder
```
Fix: correct the path or the file's permissions.

### KL008 container
Error. A `.kschem` file is cut short or malformed, so its JSON header cannot be reached. The other messages name the
magic, the version and the header length.
```text
build/hut.kschem:1:21: error KL008: cell data length 8 runs past the end of the file
```
Fix: export the schematic again.

## KL1xx: the file's schema

### KL101 type
Error. A value of the wrong type. Against the catalog's reflected field types the same check is a warning.
```json
{ "id": "my-mod", "version": 2 }
```
```text
kiln.mod.json:1:30: error KL101: expected a string, found an integer
```
Fix: write the type the message names (here `"version": "2.0.0"`).

### KL102 key-not-allowed
Error. A key in an object the format closes: its reader refuses the file.
```text
assets/cook.json:9:7: error KL102: "nmae" is not allowed here; did you mean "name"?
```
Fix: rename it (quick fix when a close match exists) or delete it.

### KL103 unknown-key
Warning. A key the reader ignores, which is usually a typo: the setting you meant has no effect, and nothing else
complains. In a manifest, Kiln.Host only logs it at load time.
```json
{ "id": "my-mod", "dependecies": { "sample-lib": "^1.2" } }
```
```text
kiln.mod.json:1:19: warning KL103: "dependecies": unknown key (Kiln.Host ignores it and logs a warning); did you mean "dependencies"?
content/x.json:2:3: warning KL103: "itemz": unknown section (kiln.content ignores it unless a service registers it with Content.RegisterSection); did you mean "items"?
```
Fix: rename it (quick fix when a close match exists) or delete it. A data-pack section your mod's code registers is not
reported.

### KL104 required
Error. A required key is missing.
```text
assets/assets.json:1:29: error KL104: missing "file"
```
Fix: add the key.

### KL105 enum
Error. A value that is not one of the allowed ones. Against the catalog's reflected enums it is a warning.
```text
content/x.json:6:15: warning KL105: "Epik" is not a value of ItemRarity (Common, Uncommon, Rare, Epic, Legendary); did you mean "Epic"?
```
Fix: use an allowed value (quick fix when a close match exists).

### KL106 pattern
Error. A string of the wrong form: an id, GUID, path or name.
```text
assets/assets.json:1:204: error KL106: "../outside.bin": a path relative to assets/ (subfolders allowed; no absolute path, no '..')
```
Fix: write it in the form the message gives.

### KL107 range
Error. A number outside its range.
```text
content/x.json:4:18: error KL107: 0 is below the minimum 1
```
Fix: use a number in range.

### KL108 size
Error. A string or list that is too long or too short, or an object with too many or too few keys.
```text
content/x.json:9:15: error KL108: an empty list: give at least one entry
```
Fix: add or remove entries.

### KL109 schema-rule
Error. A rule about how values combine: alternatives, exclusions, or combinations the format requires.
```text
assets/cook.json:6:5: error KL109: gain and normalize exclude each other
```
Fix: follow the message.

### KL110 unique-items
Error. A list that must not repeat an entry does. The message counts entries from 0.
```json
{ "id": "my-native", "dlls": ["plugin.dll", "plugin.dll"] }
```
```text
native.json:1:45: error KL110: the same entry as #0
```
Fix: remove the repeated entry (quick fix).

### KL111 format-version
Error. A `format` this Kiln does not read: its reader refuses the file.
```text
content/x.json:1:13: error KL111: format 2 is not supported (this Kiln reads 1)
```
Fix: write the format this Kiln reads, or update Kiln.

### KL112 case
Hint. A name Kiln matches without regard to case, written in another case. It works, but search and other tools will not
find it.
```text
content/x.json:5:10: hint KL112: "material_fixture_logs" matches the game's "Material_Fixture_Logs" (Kiln ignores case); write it as the game names it
```
Fix: write it as the game names it (quick fix).

## KL2xx: the manifest against the mod's content

### KL201 clients-definitions
Error. The mod's data packs define new items, recipes, templates, sequences, perks or buffs, but it says `"clients":
"none"` (the default for `side: "server"`). Vanilla clients have no definition to show.
```json
{ "id": "my-mod", "side": "server" }
```
```text
kiln.mod.json:1:19: error KL201: this mod adds a new item (and maybe more) but says no "clients" (side "server" means "none"): vanilla clients cannot show it ("[Invalid Item Stack]"); declare "clients": "kiln" (the server sends the definitions to Kiln clients and refuses vanilla ones) or "mod" (players install the mod)
```
Fix: `"clients": "kiln"` makes the server send the definitions; `"clients": "mod"` has players install the mod. The quick
fix adds `kiln`. With either one, a server running the mod refuses vanilla clients unless its `modSync` is `off`
(`kiln/config/network.json`, [network.md](../network.md)). See [manifest.md](manifest.md).

### KL202 clients-assets
Warning. The mod ships what only clients use (PNG icons, textures, model, material and UI texture resources, cooked
models, sounds or voice-overs) but does not say `"clients": "mod"`. The server never sends assets, so players without
the mod do not get them.
```text
kiln.mod.json:7:3: warning KL202: this mod ships a PNG item icon but says no "clients" (side "server" means "none"): players without the mod never see them; declare "clients": "mod" so the server's modpack delivers it
```
Fix: declare `"clients": "mod"` (quick fix).

### KL203 side-assets
Warning. Client-only assets in a `side: "server"` mod, which does not load on a joined client (only when a player hosts).
```text
kiln.mod.json:7:11: warning KL203: a PNG item icon is drawn by clients, but side "server" mods do not load on a joined client (only when a player hosts); use side "both"
```
Fix: declare `"side": "both"` (quick fix).

### KL204 side-clients
Info. `"clients": "mod"` with `side: "server"` and nothing a client would use: players must install a mod that never
loads on their game, and vanilla clients are refused for it. Game-tree nodes are the exception, because kiln.skills reads them natively on every side.
```json
{ "id": "my-mod", "side": "server", "clients": "mod" }
```
Fix: declare `"side": "both"` if clients need the mod, or `"clients": "kiln"` or `"none"` if they do not.

### KL205 entry-missing
Error. `entryAssembly` or an entry of `exports` is not in the mod folder, so Kiln.Host fails to load the mod. Skipped in a
source folder (one with a `.csproj`).
```text
kiln.mod.json:5:20: error KL205: Missing.dll is not in the mod folder (Kiln.Host fails to load the mod)
```
Fix: build the mod into this folder, or correct the name.

### KL206 undeclared-dependency
Warning. A reference to what another mod defines, without depending on it. Rules apply in load order, so the result
depends on which mod happens to load first.
```text
content/x.json:6:12: warning KL206: FixtureA_Gem comes from mod fixture-a, which this mod does not depend on (rules apply in load order: add "fixture-a" to dependencies or loadAfter)
```
Fix: add the mod to `dependencies` (or to `loadAfter` if it is optional).

### KL207 clients-skills
Error for nodes in the game's skill tree without `"clients": "mod"`: every client must build the same tree from the mod's
own files, and Kiln.Network refuses players without it. Info for a Kiln talent tree without it: the tree works, but
players without the mod only reach it through `/talents` in chat.
```text
kiln.mod.json:8:14: error KL207: this mod has nodes in the game's skill tree but says "clients": "kiln": every client must build the same tree from the mod's own files (the server sends no tree definitions); declare "clients": "mod"
kiln.mod.json:7:3: info KL207: this mod has a talent tree but says no "clients" (side "server" means "none"): players without the mod can only use it through the /talents chat command; to draw its screen on their game, declare "clients": "mod" and side "both" (vanilla clients are then refused, unless the server's modSync is off)
```
Fix: declare `"clients": "mod"` (quick fix), and for a talent tree also `"side": "both"`. A talent tree is a real choice:
`clients: "mod"` draws its screen, but a server running the mod then refuses vanilla clients unless its `modSync` is `off`,
while leaving it out keeps them able to join and use `/talents`.

### KL208 native-prefix
Warning. A Kiln native in `impact/natives.json` must be named `<modid>.<Name>`. It is an error to use the reserved prefix
`kiln`.
```text
impact/natives.json:3:14: warning KL208: a Kiln native is named "<modid>.<Name>": this mod's id is "fixture-files", not "other"
```
Fix: rename the native after your mod's id.

### KL210 clients-quests
A quest in the game's journal (the default), dialogue in the game's NPC menu (the default) or an NPC clone, in a mod that does not say `"clients": "mod"`. A client builds them at
boot from the mod's own files, and no server sends them, so a player without those files sees nothing. Error with `"clients": "none"` (or nothing, on side `server`) and with `"clients": "kiln"`.
```text
kiln.mod.json:7:14: error KL210: this mod has a quest in the game's journal (the default), dialogue in the game's NPC menu or an NPC clone but says "clients": "none": a client builds them at boot from the mod's files, so a player without those sees nothing; declare "clients": "mod" and side "both" (servers then refuse players without it). For a mod vanilla clients join, every quest says "journal": "chat" and every dialogue "native": false
```
Fix: declare `"clients": "mod"` (quick fix). For a mod vanilla clients must be able to join: `"journal": "chat"` on every quest and `"native": false` on every dialogue, which is chat lines, `/quests` and `/talk`.

### KL211 clients-vfx
Error. A block in the `vfx` or `materials` section, or a `vfx` field that names one (anything but `"game:<name>"` or a GUID), in a mod that doesn't say `"clients": "mod"`. A client builds a new VFX or material at boot
from the mod's own files, and no server sends assets. On side `server`, [KL203](#kl203-side-assets) says the same about where the mod loads.
```text
kiln.mod.json:7:14: error KL211: this mod has a new VFX but says "clients": "kiln": a client builds it at boot from the mod's own files, and the server never sends assets; declare "clients": "mod". A VFX of the game ("game:<name>") needs no such thing
```
Fix: declare `"clients": "mod"` (quick fix), or use one of the game's VFX.

### KL209 manifest-refused
Error. The real manifest reader refuses the file for a reason the schema cannot express. It is reported only when the
schema found no error in the file.
```text
kiln.mod.json:6:3: error KL209: Kiln.Host refuses this manifest: kiln.mod.json: 'other' is in both loadBefore and loadAfter
```
Fix: follow the reader's message.

## KL3xx: ids, GUIDs and names

### KL301 duplicate-guid
Error. One mod defines a GUID twice. Templates, sequences, perks and buffs share one id space.
```text
content/x.json:12:15: error KL301: GUID 7c6c2a10-9b1e-4c3a-8d7f-1e2a3b4c5d60 is defined twice in this mod (the later definition replaces the earlier one)
```
Fix: give one of them a new GUID.

### KL302 profile-duplicate-guid
Error. Two mods linted together define the same GUID (use `--profile` or lint both).
```text
content/x.json:4:15: error KL302: GUID aa6c2a10-9b1e-4c3a-8d7f-1e2a3b4c5d60 is also defined by mod fixture-a: one definition replaces the other
```
Fix: give yours a new GUID, unless you did not mean to copy the other mod's definition at all.

### KL303 game-guid
Error (needs the catalog). A definition's GUID is the game's own, or hashes to the id of a game entry.
```text
content/x.json:4:15: error KL303: GUID 0000a001-0000-4000-8000-000000000001 is the game's own item (a definition needs a new GUID)
```
Fix: generate a new GUID. To change the game's entry, write a rule on it instead of a definition.

### KL304 id-collision
Error. Two different GUIDs hash to the same id. Kiln refuses the second.
```text
content/x.json:9:15: error KL304: GUID <this GUID> hashes to id 0x1234abcd, like <the other GUID>: Kiln refuses the second one (pick a new GUID)
```
Fix: pick a new GUID. A released definition keeps its GUID, so change the newer one.

### KL305 duplicate-name
Error. A debug name is defined twice in one mod, or a talent tree has a node id twice. A variant id given twice is a
warning.
```text
content/x.json:9:17: warning KL305: variant id "dup" is defined twice in this mod
skills/trees.json:14:15: error KL305: tree my-mod:arts: node "a" twice (Kiln.Skills refuses the tree)
```
Fix: rename one of them.

### KL306 game-name
Error for items and recipes (Kiln refuses the definition); warning for other sections (selectors that name it become
ambiguous). Needs the catalog.
```text
content/x.json:5:17: error KL306: "Material_Fixture_Twigs" is the name of a game item: Kiln refuses the definition (use a prefix of your own, e.g. MyMod_...)
```
Fix: use a prefix of your own.

### KL307 profile-duplicate-name
Error. Two mods linted together define the same debug name (a warning for variant ids).
```text
content/x.json:5:17: error KL307: item name "FixtureA_Ore" is also defined by mod fixture-a
```
Fix: use a prefix of your own.

### KL308 profile-duplicate-mod
Error. Two mods linted together have the same id.
```text
kiln.mod.json:1:9: error KL308: mod id "fixture-a" is also used by mods/other: Kiln loads only one of them (the first folder in name order)
```
Fix: remove one, or give it another id.

### KL309 duplicate-asset
Warning. `assets.json` serves the same kind, GUID, type and part twice.
```text
assets/assets.json:9:30: warning KL309: icon 9e6c2a10-9b1e-4c3a-8d7f-1e2a3b4c5d60 is served twice in this file (the later entry wins)
```
Fix: delete the entry you did not mean.

### KL310 talent-cell
Error. Two nodes of a talent tree sit in one grid cell, so Kiln.Skills refuses the whole tree. A node without `col` and
`row` sits at 0, 0, so two nodes without them collide.
```text
skills/trees.json:12:7: error KL310: tree my-mod:arts: two nodes at column 0, row 0 (Kiln.Skills refuses the tree; a node without "col" and "row" sits at 0, 0)
```
Fix: give each node its own `col` and `row`.

## KL4xx: references to the game's data

### KL311 quest-reference
A quest, step, NPC, dialogue or node named in `quests/*.json` that the mod's quest files do not define. Definitions made
in C# are not visible to kiln lint, so most of these are warnings; a dialogue with no way to start is an error. A name
with a `:` is another mod's and is not checked.
```text
quests/chain.json:14:32: warning KL311: no node "thnaks" in this mod's quest files; did you mean "thanks"?
```
Fix: correct the name (quick fix), or define it.


### KL312 native-quest
What the boot build of the game's data refuses (`native/services/quests/questpack.cpp`, docs/kiln/services/npcs.md): a mod with one of these errors
has none of its quests, dialogue, clones or knowledge in the game's UI, its quests run with chat lines only, and kiln.log says why. A quest in the journal needs `name`, its steps `text` or `title`.
An NPC with dialogue in the game's menu needs `template`. `roster` needs `clone`, and its `unlock` a declared knowledge name, `0x...` or `npc:<debugName>.unlock`. `voice` names a voice-over of the mod's
`assets/cook.json`, or a GUID. A `reach` with `"site": true` needs the step's `site`.

Warnings, for what has no form in the game's UI: `toast` is a chat line (the game has no free-text notification: use a step's text or an NPC's reply), `talk` opens a chat conversation (the game's menu opens only when a player talks to the NPC).

A line's `if` is no longer limited: the game checks one knowledge query per line, and whatever one query cannot say gets a knowledge object of its own, kept by the engine.
```
quests/tobin.json:12:15: error KL312: quest tobin_kindling: a journal entry needs "name"
quests/tobin.json:40:15: warning KL312: quest tobin_kindling: "toast" is a chat line now: the game has no free-text notification. Put it in a step's text or an NPC's reply
```
Fix: as the message says.

### KL313 summon-item-text

Warning. A roster NPC's `summonItem` definition has a name of its own (`displayName`, `texts.<language>.name`), or a description
while the NPC has its `description`. The game's Survivors list shows the tool's name and description for the NPC, so every tool of the
game is named after its NPC, and the boot build replaces both with the NPC's `names` and `description`. Say what the tool is in its
`caption` or `lore`.

```
quests/tobin.json:21:9: warning KL313: npc tobin: the Survivors list shows this tool's name and description for the NPC, so the build uses the NPC's "names" and "description". Put what the tool is in its "caption" or "lore"
```

### KL314 block-reference

Warning. A field of a `blocks/*.json` file names a block that no `blocks/*.json` file of the mod defines and the mod's code does not mention, or one of the wrong kind
(a status where an effect goes, stats where a look goes, an effect where a VFX goes). Kiln.Blocks refuses that block at load. A premade's key is fine (`"class": "sword"`), and `other-mod:name` is not checked.
A `vfx` field's `"game:<name>"` and GUIDs are [KL323](#kl323-vfx-reference)'s.
With the catalog, the same goes for an item a block names (`recipe.needs`, `like`, `dropsLike`, `dropped`) that is neither a block of the mod nor an item of the game.

```
blocks/frost.json:9:22: warning KL314: no effect block "frost-bust" in this mod's blocks/*.json or code (Kiln.Blocks refuses it at load); another mod's is "mod-id:frost-bust"
```

### KL320 vfx-budget
Error. More than the game's VFX system holds, for this mod and the mods checked with it (`--profile`). Past a limit the game aborts, so at boot Kiln refuses the mod that reaches it, and none of its VFX load.
Only blocks in `blocks/*.json` are counted: a `VfxBlock` defined in code isn't, nor is what other mods define unless they are checked too.

| What | Limit | The game uses | Left for all mods |
|---|---|---|---|
| VFX | 2,048 | 1,575 (from the catalog when there is one) | 473 |
| Meshes | 1,024 | 638 | 386 |
| Mesh memory | 17,825,792 B | 17,567,564 B, and 16 KB is kept free | about 240 KB |
| Sounds in VFX | 16 | 6 | 10 |

A block can only name the built-in `quad` and `quad-xz` meshes today, 140 B each and shared by all mods, so the mesh limits don't come into play yet.
```text
blocks/many.json:4:5: error KL320: This mod asks for 474 new VFX, and the game has room for 473 (2048 less its 1575): past a limit the game aborts, so Kiln refuses the mod that reaches it and none of its VFX load
```
Fix: share effects between blocks, or split the mod so that a player chooses.

### KL321 vfx-parameter
`"parameters"` on a `vfx` field.

An error where Kiln.Blocks refuses it: on a spell's `cast`, a projectile or a status, because the game's data takes a block laid out by the VFX, which only clients have; and on a VFX block of the mod, which has no named
parameters yet. An effect can set them on a game VFX, and so can an area, an on-hit and the effects inside a status.

A warning, with the catalog, for a name or type the game VFX doesn't have: the game ignores those without a word, so this is the only place that says so. The value's form decides the type: a number is a float (`3` too), `{"int": 7}`
an int, `[x, y, z]` a float3, `"#rrggbb"` or `"#rrggbbaa"` a colour. Hover a `game:` name in VS Code, or look the VFX up with `kiln catalog search`, for what it takes. Not every parameter has a known name, because the game stores only their hashes.
```text
blocks/fx.json:7:80: warning KL321: Explosion_Fixture_Frost has no parameter "radus", and the game ignores what it doesn't have; it has radius, color, count; did you mean "radius"?
blocks/fx.json:9:92: warning KL321: "count" of Explosion_Fixture_Frost is an int, and this is a float: the game ignores it; write it as {"int": n}
blocks/fx.json:22:95: error KL321: a spell's cast cannot set a VFX's parameters: the game's data takes a block laid out by the VFX, which only clients have (Kiln.Blocks refuses it). Put the VFX on an effect, or leave them out
```

### KL322 vfx-texture
Error. A particle's or material's PNG that Kiln refuses: both sides must be powers of two, from 4 to 2048 for a particle and to 4096 for a material. A file that isn't a PNG at all is [KL503](#kl503-not-png).
```text
blocks/fx.json:5:54: error KL322: vfx/odd_6x8.png: 6x8 is not a power of two on both sides (Kiln refuses this block)
```
Fix: resize the image.

### KL323 vfx-reference
Warning (needs the catalog). A `vfx` field names a game VFX the catalog doesn't have, or one only its GUID can find. VFX have no names of their own: `"game:<name>"` finds one through a template that carries it or a sequence that plays it. Catalog
entries without `byName` have a name only as a label (`Vfx_0000f003`, a template's `DestroyOnLoot`). An unknown GUID shows nothing, because Kiln's guard stops it before the game would abort.
```text
blocks/fx.json:13:22: warning KL323: no game VFX "Explosion_Fixture_Frots" in the catalog; did you mean "Explosion_Fixture_Frost"?
blocks/fx.json:14:23: warning KL323: "Prop_Fixture_Crate_DestroyOnLoot" is the catalog's label for this VFX: nothing at run time finds it by that name; use its GUID
```
Fix: correct the name, or use the GUID (both are quick fixes).

### KL324 vfx-flipbook
Warning. A `flipbook` grid whose PNG can't divide evenly into it, so frames will be cut. A grid that divides but doesn't match the art can't be detected.
```text
blocks/fx.json:8:86: warning KL324: vfx/good_8x8.png: 8x8 doesn't divide into 3 columns and 2 rows: frames will be cut
```
Fix: correct `columns` and `rows`, or the image.

### KL330 material-budget
Error. More `buildingMaterials` than the game has ids for: 49 across every mod, counted over the mods checked together (a profile). A material's id is a byte in a cell, so the room
can't grow. At boot, kiln.worldtables gives ids in sorted GUID order and refuses the rest by name, so which block is over depends on the other mods.
```text
blocks/walls.json:3:5: error KL330: This mod has 50 building materials, and the game has ids for 49: kiln.worldtables refuses those past that by name, starting from the last in sorted GUID order ("m0" is one of this mod's)
```
Fix: fewer materials. A material an absent mod left in a world also keeps its id; `WorldTables.Forget` frees it.

### KL331 material-count
Warning. A mod with 5 or more `buildingMaterials`. Every mod shares the 49 ids, so one mod with many leaves the others little.
```text
blocks/walls.json:3:5: warning KL331: this mod has 6 building materials: all mods share 49 ids, and past that the game can't take more
```
Fix: keep a mod's materials few, or say on its page how many it takes.

### KL332 material-reference
Warning (needs the catalog). A `materialTweaks` block's `material` that is not a game material: the block is refused at boot. Names come from the item that places the material
(`StoneRough`, `WoodFancy`, `TerrainStone`); `Cell<id>` and the material's GUID work too.
```text
blocks/tweaks.json:2:27: warning KL332: "StoneRuogh" is not a game material the catalog knows; a block that names one is refused at boot; did you mean "StoneRough"?
```
Fix: correct the name (quick fix when a close match exists), or use a `GameMaterials` constant.

### KL333 surface-texture
A building surface's PNG (`surfaces`, or `surface` in `buildingMaterials` and `materialTweaks`), as each client's kiln.surfaces will cook it. Error: a size it refuses, not a power of
two on both sides or outside 4 to 4096; the material then looks like Rough Stone. Warning: a greyscale normal map (flat), and an albedo or emissive tagged linear, which is read as
sRGB colour. Info: a data map in colour (only its red is read), one tagged sRGB (its values are read as they are), 16 bits a channel (read at 8). A file that isn't there or isn't
a PNG is [KL501](#kl501-file-missing) or [KL503](#kl503-not-png).
```text
blocks/s.json:3:37: error KL333: tex/odd.png: 6x8 is not a power of two on both sides (kiln.surfaces refuses this surface, and the material looks like Rough Stone)
blocks/s.json:6:84: warning KL333: tex/normal_grey.png is greyscale: a normal map's red and green are its direction, so this one is flat
```
Fix: resize or re-save the image: colour maps (albedo, emissive) in sRGB, data maps (roughness, metallic, reflectance, height, AO) as greyscale, normal maps as RGB.

### KL401 unknown-reference
Warning by default; an error where the reader refuses the file or the node (game-tree links, a talent node's `requires`).
A name, id or GUID that neither the game nor the mods have. References to the game need the catalog.
```text
content/x.json:6:12: warning KL401: no item with GUID 9e6c2a10-9b1e-4c3a-8d7f-1e2a3b4c5d60 in the game or the mods
skills/x.json:51:6: error KL401: no skill node with id 0xdeadbeef in the game or the mods
```
Fix: correct the reference (quick fix when a close match exists), or add the mod that defines it to your dependencies.

### KL402 unknown-field
Warning (needs the catalog). A field path the game's reflection does not have.
```text
content/x.json:7:7: warning KL402: "maxStackSise": no such item field in the game's reflection (Kiln reports "unknown field"); did you mean "maxStackSize"?
```
Fix: rename it (quick fix when a close match exists). The catalog's field lists are in [catalogs.md](catalogs.md).

### KL403 glob-matches-nothing
Warning (needs the catalog). A selector glob that matches nothing, so the rule does nothing.
```text
content/x.json:3:5: warning KL403: "Food_*" matches no item (Kiln warns "matches nothing")
```
Fix: correct the pattern.

### KL404 unknown-language
Info (needs the catalog). A language key no game language uses, so that text never shows.
```text
content/x.json:8:9: info KL404: no game language is "xx" (the game has en-us, de-de, pt-br): this text never shows
```
Fix: use one of the languages listed.

### KL405 asset-mode
Error (needs the catalog). An `assets.json` resource says `"override"` but the game has no such resource, so kiln.assets
refuses the entry, or says `"new"` for a resource the game has.
```text
assets/assets.json:1:60: error KL405: "mode": "override" but the game has no resource 00011001-0000-4000-8000-0000000000aa: kiln.assets refuses the entry
```
Fix: correct the GUID, or use the other mode.

### KL406 crafted-rolled
Warning. A recipe in the mod's data pack makes an item that rolls its rarity or level: a new item with `"rolledLoot": true`,
`"generateRarity": true` or a level range in its `fields`, or (with the catalog) a game loot weapon. The recipe tooltip shows
the item's `rarity` and lowest level, but a craft passes neither to the game, so the crafted item differs from it (a Common at
a random level). New items are fixed by default: docs/kiln/services/content.md "Rarity, level and perks of new gear". Definitions
from code are not read here; they get the same warning in kiln.log and in `NewContent.Definitions()`.
```text
content/items.json:18:46: warning KL406: recipe "My_Sword_Recipe" makes "My_Sword", which rolls ("rolledLoot": true): a craft comes out as the lowest allowed rarity or a random level while the recipe tooltip shows its rarity and lowest level; remove that for a fixed item
```
Fix: remove the opt-in, or take the recipe away from a loot item. For a game item, base a new item on it, or add a rule with
`"generateRarity": false` and one level.

A block in `blocks/*.json` gets the same warning for `"loot": { "rolled": true }` with a `recipe`; Kiln.Blocks refuses that block at load.

## KL5xx: files, assets and cook recipes

### KL501 file-missing
Error. A file the mod names is not there.
```text
assets/assets.json:3:40: error KL501: icons/missing.png: no such file (/home/me/MyMod/assets/icons/missing.png)
```
Fix: add the file or correct the path. Paths are relative to the folder the format says (`assets/` in `assets.json`).

### KL502 cook-output-missing
Info. A file the mod's cook recipe makes is not there yet. Normal in a source folder.
```text
assets/assets.json:3:40: info KL502: built/two.bin is made by the cook and is not there yet: run the mod's cook recipe (tools/modelcook, tools/impact, or the launcher at install)
```
Fix: run the cook, or ignore it.

### KL503 not-png
Error. A file named as an icon or texture is not a PNG (its first bytes say so, whatever its extension).
```text
assets/assets.json:3:40: error KL503: icons/gem.png is not a PNG file (kiln.assets decodes PNG only)
```
Fix: export it as PNG.

### KL504 blob-size
Error. A blob override's file length differs from the size in its hash, so kiln.assets refuses it.
```text
assets/assets.json:5:50: error KL504: the hash says 12 bytes, built/m.blob has 10: kiln.assets refuses the override
```
Fix: re-cook, or use `--update-manifest` to write the matching entries.

### KL505 patch-offset
Error. A `patch` offset runs past the end of the resource file.
```text
assets/assets.json:6:30: error KL505: offset 96 + 16 bytes runs past the end of built/m.bin (100 bytes)
```
Fix: correct the offset, or re-cook the resource.

### KL510 cook-base
Error. A step's `base` is not one of the recipe's inputs, or is an input of the wrong type. The cook stops there.
```text
assets/cook.json:8:15: error KL510: step 0 (model): base cue is a keen::SoundContainerResource, needs keen::RenderModel
```
Fix: name an input of the type the message gives.

### KL511 cook-input
Error. An `@input` reference to no input, or a cue's take that is not a sound of this recipe, an `@input` or a GUID.
```text
assets/cook.json:9:15: error KL511: @modle: no such input; did you mean @model?
```
Fix: correct the name (quick fix when a close match exists), or declare the input under `"inputs"`.

### KL512 cook-unlisted
Warning. A model or Impact step writes a file `assets.json` does not list, so kiln.assets never serves it.
```text
assets/cook.json:10:40: warning KL512: this step writes built/unlisted.bin, which assets/assets.json does not list: kiln.assets will not serve it (cook with --update-manifest)
```
Fix: cook with `--update-manifest`. soundcook writes its own entries, so sound steps are not checked.

### KL513 cook-stem
Error. Two steps write the same output: a `stem` used twice (the outputs overwrite each other), or a sound, cue or
voice-over name defined twice (soundcook stops).
```text
assets/cook.json:12:30: error KL513: stem "two" is used by an earlier step too: the outputs overwrite each other
```
Fix: give each step its own name or stem.

### KL514 native-hash
Error. A native's `hash` is not FNV-1a of its name, so its ECALLs would call nothing; or its `argc` differs from its
`args`.
```text
impact/natives.json:4:15: error KL514: hash 0x12345678 is not FNV-1a("fixture-files.Ping") = 0x8807b1ea: the ECALLs would call nothing
```
Fix: write the hash the message gives (quick fix), or correct `argc`.

### KL515 schematic-size
Error. A schematic over Kiln.Build's limit of 4,194,304 cells.
```text
build/hut.kschem:1:40: error KL515: 33554432 cells: Kiln.Build reads schematics of at most 4,194,304 cells
```
Fix: split it, or export a smaller area.

### KL516 cook-skeleton
Error. A model step's `skeleton` is neither a `skeleton` step of the recipe nor an input of type `keen::ModelHierarchyResource`.
```text
assets/cook.json:15:60: error KL516: step 7 (model): no skeleton step is named "golemm" in this recipe (a game skeleton is @input); did you mean "golem"?
```
Fix: name the step, or add the game's skeleton to `inputs`. See [Skinned models](models.md#skinned-models).

## KL6xx: licences and what may be shipped

### KL601 no-license
Info. The manifest names no licence, so others cannot tell how they may use the mod.
```text
kiln.mod.json:1:1: info KL601: no "license": say how others may use the mod (an SPDX expression, e.g. MIT)
```
Fix: add `"license"` with the licence you choose. In a Kiln.Sdk project, set `<PackageLicenseExpression>`. The quick fix
inserts `MIT` as a starting point, so change it if that is not what you intend.

### KL602 bad-license
Warning. A licence that is not an SPDX expression, so tools cannot read it. `LicenseRef-<name>` is the form for your own
terms.
```text
kiln.mod.json:6:14: warning KL602: "MIT License" is not an SPDX licence expression; did you mean "MIT"?
```
Fix: write the SPDX id (quick fix when one is close).

### KL603 third-party-notice
Warning. The mod folder holds DLLs it does not build (not `entryAssembly`, `exports`, `dlls` or `Kiln.*`), with no licence
notice beside them. Their licences usually require one.
```text
kiln.mod.json:1:1: warning KL603: the mod bundles a DLL that it does not build (Newtonsoft.Json.dll): add their licence texts (a LICENSE / THIRD-PARTY-NOTICES file or a licenses/ folder in the mod)
```
Fix: add a file named `LICENSE`, `NOTICE`, `THIRD-PARTY...`, `COPYING` or `CREDITS` (or a `licenses/` folder) to the mod.

### KL604 game-data
Error. A game file inside the mod (`*.kfc`, `*.kfc_resources`, `enshrouded.exe`, `enshrouded_server.exe`,
`enshrouded_*.dat`). Mods must not redistribute the game.
```text
game.kfc:1:1: error KL604: a game file inside the mod: mods ship sources and a cook recipe, never the game's data (docs/kiln/modding/models.md)
```
Fix: remove it. Ship your sources and a cook recipe ([models.md](models.md)).

### KL605 cooked-committed
Warning. The mod has `assets/cooked/` in a git work tree, and no `.gitignore` from the mod up to the repository root
mentions `cooked`. Those files are made from the game's data.
```text
kiln.mod.json:1:1: warning KL605: assets/cooked/ holds files made from the game's data: add "assets/cooked/" to the mod's .gitignore (they must never be committed or shipped)
```
Fix: add `assets/cooked/` to the mod's `.gitignore`. If they are already committed, remove them from git too.

## KL9xx: the linter itself

### KL901 no-catalog
Info. No game-data catalog was found, so the references it counts were not checked.
```text
kiln.mod.json:1:1: info KL901: no game-data catalog: 2 reference(s) to the game's items, templates, ... were not checked (make one with `kiln catalog`, or pass --catalog)
```
Fix: run `kiln catalog build`, or pass `--catalog`.

### KL902 catalog-unreadable
Warning. The catalog you named is missing, or a catalog cannot be read, so references were not checked.
```text
kiln.mod.json:1:1: warning KL902: the game-data catalog cannot be used: /tmp/nope: no catalog there (client.json / server.json)
```
Fix: correct the path, or rebuild the catalog (`kiln catalog build`).

### KL903 nothing-to-lint
Info. The path holds no mod and no file kiln lint knows, or the file you named is not one it maps to a schema.
```text
mods/empty:1:1: info KL903: no mod (kiln.mod.json, native.json) and no file kiln lint knows in this folder
```
Fix: point it at a mod folder or a known file ([what gets read](#what-gets-read)).

### KL904 manifest-not-built
Info. A Kiln.Sdk project whose generated `kiln.mod.json` does not exist yet, so its manifest cannot be checked.
```text
MyMod.csproj:1:1: info KL904: a Kiln.Sdk project that was not built yet: build it once (dotnet build) so kiln lint can check the kiln.mod.json it generates
```
Fix: run `dotnet build` once.
