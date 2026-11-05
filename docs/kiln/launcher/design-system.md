# Kiln launcher design system

 Code: `launcher/src/ui/` (one barrel: `import { Button, Card } from "../ui"`), styles
`launcher/src/ui/ui.css` (loaded once by `theme/base.css`), tokens `launcher/src/theme/tokens.css` (generated from
`branding/palette.json` by `bun run tokens`). Theme and design: frostdev.

## Rules
- Dark only. Backgrounds `--k-bg-0`…`--k-bg-3`, lines `--k-line`, text `--k-text-0`…`--k-text-3`.
- Ember (`--k-ember-500` `#ff6a1a`) marks the one action that matters; gold is a second accent; the molten gradient
  (`--k-grad-molten`) is for the hero Play button, active tabs and progress bars only.
- Text on ember or gold fills is `--k-text-on-ember` (`#0f0d0c`), never white.
- Keyboard focus always shows the ember ring (`:focus-visible` outline + `--k-glow`). Every interactive element is a
  real button/link/input; icon-only buttons carry a label (`IconButton label=…`).
- Type: Fraunces for titles (page, card, modal; `opsz` set per size), Inter for UI, JetBrains Mono for ids, paths,
  hashes, logs. Bundled locally (`src/assets/fonts/`, OFL licences next to them), no web-font URLs.
- Copy: short labels, one-line hints, no marketing. Say what happens ("Restore", "Close the game first."), not why
  Kiln is great.
- Icons: lucide-react, 15–18 px, stroke icons only.
- Brand: `branding/logo/mark.svg` in the sidebar, the splash and About; never recoloured or stretched.

## Components
| Component | Props (short) | Notes |
|---|---|---|
| `Button` | `variant: primary \| molten \| secondary \| ghost \| danger \| danger-fill`, `size: s \| m \| l`, `icon`, `loading` | primary = ember fill + dark text |
| `IconButton` | `label`, `icon`, `variant`, `size` | `label` is the accessible name and tooltip |
| `Card` | `title`, `sub`, `actions`, `footer`, `glow`, `flush` | `glow` for the one live/important card |
| `Panel` | `title`, `actions` | an unframed titled section |
| `Page`, `PageHeader` | `wide`; `title`, `sub`, `actions` | page padding and the Fraunces title |
| `KeyValue` | `rows: [label, value][]` (falsy rows skipped) | two-column facts |
| `Tabs`, `NavTabs`, `Segmented` | `items/options`, `value`, `onChange`; `NavTabs` items are links | arrow keys move between tabs |
| `Table<R>` | `columns: {key, header, width?, align?, render?, sort?}[]`, `rows`, `rowKey`, `rowHeight`, `maxHeight`, `onRowClick`, `selectedKey`, `empty` | virtualized (TanStack Virtual), grid columns |
| `Field`, `TextInput`, `TextArea`, `Select`, `NumberInput`, `Slider`, `Toggle` | `NumberInput`/`Slider` commit on blur/Enter/release (`onCommit`) | `Toggle` is a `role=switch` checkbox |
| `Modal` | `open`, `onClose`, `title`, `footer`, `width`, `locked` | Esc/backdrop close, focus trap, `data-autofocus` |
| `ConfirmDanger` | `title`, `message`, `word`, `action`, `onConfirm` | the action enables only when `word` is typed |
| `toast.ok/info/error`, `<Toaster/>` | `toast.error(err, title?)` takes any rejection | shell mounts the Toaster |
| `TaskProgress`, `TaskTray`, `runTask`, `waitTask`, `useTasks`, `useTaskFeed` | `runTask(() => cmd(...), {done, failed, quiet})` | fed by `kiln://task`; the shell mounts the feed and the tray |
| `LogView` | `lines \| text`, `follow`, `height`, `filter`, `lineNumbers`, `classify` | virtualized; follows at the bottom; colours `[E]`/`[W]`/`[D]` (with or without a timestamp) |
| `JsonView` | `value`, `open` (levels), `maxHeight` | collapsible tree |
| `StatusPill`, `Badge`, `Banner`, `Kbd` | `tone: ok \| warn \| err \| info \| idle \| ember`, `pulse` | `Banner` for refusals and warnings in a page |
| `EmptyState`, `Skeleton` | `icon`, `title`, `hint`, `action` | one title + one line |
| `DropZone`, `PathPicker`, `pickPaths` | `onPaths`, `pick`, `pickFolder`; `value`, `onChange`, `directory`, `filters` | Tauri drops give real paths (webview drag-drop events); the browser mock passes file names |
| `SchemaForm` | `schema: SchemaSnapshot`, `values`, `onChange(path, value)`, `onReset(path)`, `errors`, `pending`, `filter` | see below |
| `SortableList` | `items`, `itemKey`, `render`, `onReorder` | pointer-event drag (works where the webview disables HTML5 DnD), Alt+Up/Down, arrow buttons |
| `Menu` | `label`, `icon`, `items: {label, icon, onSelect, danger, disabled}[]`, `variant`, `size` | small dropdown |
| `Mark`, `Splash`, `Sparkline` | `size`; `done`; `values` | brand mark, start splash with the molten glow |
| `fmtBytes`, `fmtAgo`, `fmtTime`, `shortHash`, `errorText` | | formatters |

Shared pieces outside `ui/` (also usable by the server and admin pages): `components/ConfigEditor.tsx`
(`<ConfigEditor target={{ profile | server | game: id }} />`: files, SchemaForm, raw JSONC), `components/SaveBackups.tsx`,
`LogsFor` in `pages/LogsPage.tsx` (`target={{ server: id }}`).

## SchemaForm
Reads a `.schema` snapshot (contracts.md 5.5) and never writes: the page saves through `config_set` /
`config_reset` (comments kept by the backend).

| Entry type | Control |
|---|---|
| `bool` | Toggle |
| `int`, `float` | number box; with `min` and `max`: slider + number box |
| `enum` | Select over `options` |
| `duration` | text, checked against `^(\d+(\.\d+)?(ms\|s\|m\|h\|d))+$` (`"30s"`, `"5m"`, `"1h30m"`) |
| `list<T>` | chips + an add box (items parsed as T) |
| `string` | text |

Badges: `restart` (requiresRestart), `synced` (sent to Kiln clients; also on sections), `pending` (from `pending`).
A changed row gets a gold edge, an invalid one a red edge and the message; changed rows show the default and a reset
button. Sections come from `schema.sections` (entry path prefix).
