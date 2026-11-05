# Vendored: Enshrouded Save Editor

| | |
|---|---|
| Upstream | https://github.com/nicolasferreiraxn/Enshrouded-Save-Editor |
| Commit | `b277f6bafd1b59f675b09204322e3554b598f779` (1.0.2, 2026-10-25, "devolver os icones da arvore de habilidades") |
| Licence | MIT, Nicolas Ferreira ([LICENSE](LICENSE); also `licenses/Enshrouded-Save-Editor-LICENSE.txt`) |
| Copied | `src/`, `shared/`, `data/` |

The editor runs inside the Kiln Launcher at `/editor` (lazy route, `pages/EditorPage.tsx` → `KilnEditor.jsx`). Its
Rust side (upstream `src-tauri/src/main.rs`) is replaced by the launcher's `editor_*` commands
(`launcher/src-tauri/src/commands/editor.rs` on `kiln_manage::saves::editor`, which knows Linux/Proton save paths;
upstream only knew `C:/Program Files*/Steam/userdata`).

## Local changes
Unchanged: `data/*`, `shared/*` except `character.mjs`, and every `src/` file not listed here.

| File | Change |
|---|---|
| `src/main.jsx`, `src/index.html` | removed: the launcher mounts the editor (`KilnEditor.jsx`) |
| `src/Servidor.jsx`, `src/Jogadores.jsx` | removed: the SFTP "Servidor" tab (Guardian server copies) is hidden; the launcher manages servers on its own pages |
| `src/App.jsx` | `TABS` without `srv`; no Servidor/Jogadores render, no "Configurar servidor…" menu entry; the 2560×1440 stage fits the editor's own box (ResizeObserver) instead of the window |
| `src/platform.js` | rewritten: `api` is always `tauri-api.js`; `ASSET_BASE` is the launcher's `kiln-asset` protocol (`kiln-asset://localhost/editor/`, Windows `http://kiln-asset.localhost/editor/`), `/assets-local/editor/` in the browser dev server; `registerFonts` runs once; new `assetsPresent()` |
| `src/tauri-api.js` | its `invoke` names map to the launcher's `editor_*` commands through `launcher/src/api/commands.ts` (errors wrapped into `Error`); the Guardian server-copy code is removed; `browseSave` uses the launcher's dialog helper; gameart: a save the strict read rejects is read again accepting any item id (stacks of mods that are gone), with a read-only note (saving still refuses unknown ids) |
| `src/gamedata.js` | gameart: `mergeModded(overlay, version)` merges the items of Kiln mods (the launcher's `modded/items.json`) into `items`/extras (copies of the base item's entry with their own name, rarity, stack, icon; icon overrides of vanilla items; undoable); `itemName` of an id with no entry: "Unknown item (0x…)" |
| `src/itemstats.js` | gameart: `copiarEstatisticas(de, para)`: a mod item uses its base item's attribute lines |
| `src/ui.js` | gameart: `itemIcon` uses an entry's own `icon` (mod items) when it has one |
| `src/Catalog.jsx` | gameart: the item list is built when the catalog opens (mod items merge after the module loads) |
| `shared/character.mjs` | gameart: when no equipment list of at least 4 pieces fits, the search accepts a shorter one (new characters wear fewer; the inventory header and the mask must still close; checked: byte-identical round trips) |
| `src/i18n/en.json` | gameart: "Unknown item (0x{h})", the read-only note for saves with unknown items |
| `src/i18n.js` | without a saved choice, anything but Portuguese defaults to English (upstream: Portuguese) |
| `src/styles.css` | scoped under `.kiln-editor` by `launcher/scripts/scope-editor-css.ts` (`:root`/`html`/`body`/`#root` → `.kiln-editor`, every selector prefixed, `.viewport` absolute instead of fixed). Regenerate, don't hand-edit |

Added by Kiln: `KilnEditor.jsx` (scope wrapper, fonts, missing-asset placeholders, the one-line "Preparing game art…",
the mod overlay loaded and merged before a save is read, remounts when the art or the overlay changes), `KilnEditor.d.ts`,
`kiln-editor.css`, this file.

`shared/recusa.mjs` is unused now (it served the server tab) and stays for a clean re-vendor.

## Game assets
Fonts, item icons and UI textures (upstream `build-assets/game/`, extracted from the game) are game files: never
committed, never bundled. The launcher makes them from the user's installed client into `<data>/editor-assets/`
(`kiln_manage::gameart`, docs/kiln/launcher/gameart.md: the same names and sizes as upstream's set, 4,585 of its 4,587
files). The launcher does it on its own (no button), and adds the items of Kiln mods as an overlay
(`<data>/editor-assets/modded/`). While the art is missing: placeholders and a one-line "Preparing game art…".
`tools/launcher/fetch-editor-assets.sh` copies upstream's set into `launcher/assets-local/editor/` (gitignored; the vite
dev server) as a dev fallback. After re-vendoring, check gameart.md "Editor updates" (texture names, tints, fonts).

## Updating
1. Copy upstream `src`, `shared`, `data` over these folders (keep the Kiln files listed above).
2. Re-apply the table above (`git diff` against this commit shows the exact edits).
3. `bun scripts/scope-editor-css.ts <upstream>/src/styles.css src/editor/src/styles.css`.
4. Update the commit line here.

Kiln changes to the copied files: skill tree class labels go through `t()` with English entries in `src/i18n/en.json`; the catalog shows the other-language item name only to Portuguese readers.
