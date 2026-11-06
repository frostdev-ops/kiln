# Kiln launcher design system ("Hearth")

The launcher is a place: a stone kiln mouth in the dark. The fire is the only light, and the mouth is the Play button.
Code: `launcher/src/ui/` (one barrel: `import { Button, Table } from "../ui"`), styles `launcher/src/ui/ui.css`; the
shell (stage, header, sheet, mouth) in `launcher/src/theme/base.css` and `launcher/src/shell/`; tokens
`launcher/src/theme/tokens.css`, generated from `branding/palette.json` by `bun run tokens`. Theme and design: frostdev.

## Rules
- **Fire is the only light.** Nothing glows unless it is hot: the mouth, the Play state, a running task, a lit mod.
- **Ember means press or running.** One ember fill (`Button variant="primary"`) per view; it never decorates.
- **Say it first.** Every page has a name and one live sentence that answers "what's the state?" (`PageHeader sub`).
- **Blackletter is for names.** Grenze Gotisch sets profile, server and page names at 36 px and up; data never uses it,
  except load-order numerals.
- **No cards.** Sheets, wells and rules separate things. A border or fill has to mark a real object. No pills, no
  chips, no coloured left edges.
- **Small caps, not tracked caps.** Labels, table heads, tabs and nav use Alegreya Sans SC. Nothing is uppercase and
  tracked.
- **Glaze colours carry state,** always with a word: celadon (ok), cobalt (info), amber (warn), iron red (err).
- **Destructive means typed.** Removing or uninstalling opens the arch dialog and asks for the word.
- Focus: a 2 px gold outline with a 2 px offset; a field's well rim catches flame instead. Every control is a real
  button, link or input; icon-only buttons carry a label (`IconButton label=…`). Text on ember or gold is
  `--k-text-on-ember` (`#170803`), never white.
- Icons: text first. lucide-react only where a control has no room for a word, always labelled; the nav has none.
  A `Button` with words never shows its `icon`. The flame toggle is the one custom glyph.
- Copy: short, plain, specific. Say what happens ("Restore", "Close the game first.").

## Colour (`branding/palette.json`)
| Name | Token | Value | Use |
|---|---|---|---|
| Soot 0..3 | `--k-bg-0`..`3` | `#080504` `#110c09` `#1a120e` `#251a14` | stage; sheet; popover, toast, working dialog; banner strip |
| Well | `--k-well` | `#0a0705` | inputs and logs, set into the sheet |
| Lines | `--k-line`, `--k-line-strong`, `--k-line-hot` | warm white at 13%, 26%, 44% | row rule; control edge and table head; hover or strong edge (inset shadows: no added height) |
| Bone, Ash 1, Ash 2 | `--k-text-0..2` | `#f5e9db` `#dccbba` `#ab9682` | text, secondary text, labels and hints |
| Ash 3 | `--k-text-3` | `#7d6a5a` | disabled and unlit only (3.8:1: never text people must read) |
| Ember, Flame | `--k-ember-500`, `--k-ember-400` | `#ff6a1a` `#ff8f45` | the hot fill; numerals, focus rim, chart line |
| Gold | `--k-gold-400` | `#f9c74f` | diamond markers, focus ring, end points |
| Celadon, Cobalt, Amber, Iron red | `--k-ok`, `--k-info`, `--k-warn`, `--k-err` | `#8fd6b4` `#8cbbe3` `#f9c74f` `#ff7d66` | state words |

Radii: s 6 (buttons, inputs, hovered rows), m 10 (logs, popovers, toasts, working dialogs, the map), l 20 (the sheet's
top corners only). The other ember and gold steps stay for charts and the in-game theme. Logo colours are unchanged.

## Type
| Role | Face, size, line |
|---|---|
| Hero name (Play) | Grenze Gotisch 600, `clamp(64px, (100vw − 590px) / 5.2, 148px)`, .95; the chevron wraps with the last word |
| Page name | Grenze Gotisch 600, 52–60 px (dialog 50, working dialog and empty page 36, load-order numerals 36) |
| Page sentence | Alegreya Sans 400, 20–25 px, 1.25 |
| UI | Alegreya Sans 400–700, 16–20 px (base 17 on 1.35) |
| Label, table head, tab, nav | Alegreya Sans SC 500, 15–21 px |
| Data, ids, logs | Inconsolata at 90% width, 14–17 px, tabular figures |

Alegreya Sans has old-style figures; where digits align, use Inconsolata or `font-feature-settings: "lnum", "tnum"`.
Grenze Gotisch has no italic: never fake one. The fonts are bundled (`src/assets/fonts/`, woff2 subsets of the
google/fonts releases, each with its OFL licence beside it): no web-font URLs. Tokens: `--k-font-display`, `-ui`,
`-label`, `-mono`; classes `k-d`, `k-sc`, `k-mono`.

## Layout
- Window 1280×800, minimum 1024×640: every page fits the minimum. Lists scroll inside the sheet, never the window.
- Spacing steps 4 8 12 16 20 24 32 40 (2, 6, 10, 14, 18 inside controls and rows).
- Header 76 high, 40 left (28 under 1180 wide, where the logo shows the mark only and nav words are 17.5 px), 84 right
  (room for the pilot light). Nav words size to their text; the gold diamond sits under the active one.
- Sheet: 24 from the left and right, top 76, bottom 0, 20 px top corners. Page padding about 20 top, 32 left, 28 right.
- Columns gap 24–30; asides are fixed: 204–232 for facts (`SplitPage`), 300 for forms (`SplitPage form`). A server's
  grouped list is 130 wide.
- Rows: table 46, compact list 44, load order 48; one line each, ids beside names (`NameId`). Log 14 on 21.
- Controls: button 38 (compact 32), input 36 (compact 30–32). A hit target never drops under 30.

## The shell
`AppShell` stacks the stage (static for now: the mouth's glow), the header (logo, nav, tasks, Settings, About, the
game's state word), the Play page in front of the stage, the sheet that rises over it for every other page (660 ms
`cubic-bezier(.2,1,.25,1)`; it drops in 460 ms), the mouth, toasts and the splash. The mouth (`.k-door`, an arch) is
the Play button: on Play it is `min(500, 40% of the window)` wide, 84 above the bottom and 48 from the right; on a
sheet page it shrinks to the pilot light (36 wide, 24 from the right, 7 from the top) and is still the Play button.
By game state: no game or no Kiln: unlit, "Add game" or "Install"; updating: embers, amber word; stopped: idle fire;
starting: flare and cones; running: the elapsed time, dim; vanilla: cobalt word; safe idle: low fire, amber word.
`shell/play.ts` (`usePlayControl`) shares the state and `launch_play` between the mouth and the Play page.
The launcher update sits in the header beside the tasks (`shell/UpdateNotice.tsx`): a state word (cobalt "Update",
ember "Updating", celadon "Restart" and "Updated") and its notice, a popover like the task list that drops open by
itself when an update is found or was just installed, with no backdrop: one line and its buttons, never an
explanation (details go to launcher.log).
A soft warm light follows the pointer across the sheet (`--mx`, `--my`).

## Components
| Component | Props (short) | Notes |
|---|---|---|
| `Button` | `variant: primary \| secondary \| ghost \| danger \| danger-fill` (`molten` = primary), `size: s \| m \| l`, `loading` | primary is the hot fill; danger-fill only after a typed word |
| `IconButton` | `label`, `icon`, `variant`, `size` | `label` is the accessible name and tooltip |
| `Page`, `PageHeader`, `SplitPage` | `fill` (lists scroll inside); `title`, `sub` (the live sentence), `actions`, `meta` (id, state word beside the name); `aside`, `form` | |
| `Card` (= `Section`), `Panel` | `title`, `sub`, `actions`, `footer` | a titled group on the sheet: small-caps head, hint on the right, no frame |
| `KeyValue` | `rows: [label, value][]` | facts |
| `StatusPill`, `Badge` | `tone: ok \| warn \| err \| info \| idle \| ember` | state words (small caps in the glaze colour); no pill |
| `Banner` | `tone`, `title` (the state word), `actions`, children (one sentence) | a flat Soot 3 strip |
| `Tabs`, `NavTabs` | `items`, `value`, `onChange` | small caps with an ember line; arrow keys move |
| `Segmented` | `options`, `value`, `variant: line \| side` | words with an ember line; `side`: large small caps with a sliding gold diamond |
| `Table<R>` | `columns`, `rows`, `rowKey`, `rowHeight` (46), `fill`, `onRowClick`, `selectedKey`, `rowClass`, `empty` | virtualized; hover 6% warm, selected 12%; a sorted head shows a gold diamond; numbers right-aligned |
| `NameId` | `name`, `id`, `dim` | a name with its id beside it, one line |
| `Field`, `TextInput`, `TextArea`, `Select`, `NumberInput`, `Slider`, `Toggle` | | fields sit in wells; Toggle is a switch (a setting); the check box for picking rows is `<input type="checkbox" className="k-check">` |
| `WellInput` | `label`, `note`, `compact` | a well with its label inside ("Filter", "Search items") |
| `FlameToggle` | `on`, `onChange`, `label` | light a mod or put it out (mod lists) |
| `Modal` | `open`, `onClose`, `title`, `footer`, `width`, `locked`, `arch` | working dialog: Soot 2, Grenze 36 title, actions bottom right; `arch`: the pointed arch, warnings only |
| `ConfirmDanger`, `ArchText` | `title`, `message`, `word`, `action`, `onConfirm` | in the arch; the danger fill enables when the word matches |
| `toast.ok/info/error`, `Toaster` | | bottom left; 2.6 s; errors stay until closed |
| `TaskProgress`, `TaskTray`, `Cones`, `Cone`, `runTask` | `step` | pyrometric cones in place of a progress bar (upright, bending, slumped); the tray is the header's label and three small cones |
| `LogView` | `lines \| text`, `follow`, `fill`, `filter` | Inconsolata in a well; [E] iron red, [W] amber, [I] Ash 1, [D] Ash 2; matches get a 22% flame fill; new lines cool from flame to ash in 900 ms |
| `TickChart`, `Sparkline` | `values`, `onHover` | flame line 2 px, 16% area, gold diamond at the end, faint grid, crosshair; never two y-scales |
| `EmptyState` | `title`, `hint`, `action`, `page` | in a table: one italic line; `page`: a small unlit arch, Grenze 36, one line, one action |
| `Skeleton` | | rows at real height in Soot 2, no shimmer; a spinner only inside a busy button |
| `DropZone` | `onPaths`, `variant: niche \| row`, `busy` | the niche is an arch that fills with fire while something is dragged over it or imports |
| `SchemaForm` | see below | |
| `SortableList`, `Menu`, `JsonView`, `PathPicker` | | row actions sit in a labelled `···` menu unless there is only one |
| `Mark`, `Lockup`, `Splash` | | `branding/logo`; never recoloured or stretched |

Shared pieces outside `ui/`: `components/ConfigEditor.tsx`, `components/SaveBackups.tsx`, `LogsFor` in
`pages/LogsPage.tsx`, `pages/ticks.tsx` (tick stats).

## Screens without a mock
- Tables: small-caps heads, no zebra, one line per row; row actions in a labelled `···` menu.
- Master and detail (Items, Players, Mods): the table in the main column, a 300 wide form aside for the selected row.
- Servers: one grouped list (Run, World, Data, Files: `shell/serverNav.ts`) beside the server's name, its live
  sentence (state, players, the admin connection) and Start/Restart/Stop; other server actions in a `···` menu.
  Configs, Mods and the console are one page each: live while the server runs, the files while it is stopped.
- Admin pages: the connection shows in the server's sentence; reconnect and the transport sit in the `···` menu, or in
  the banner when disconnected. A page missing an op says "Needs a newer Kiln.Admin" in one line.
- Logs: a full-height well, small-caps tabs, Follow, Pause and Filter in the toolbar.
- Forms: one column, max 720; changed values get a gold diamond and show the default with Reset.
- The vendored Save Editor (`src/editor/`) keeps its own CSS inside a full-height sheet.

## SchemaForm
Reads a `.schema` snapshot (contracts.md 5.5) and never writes: the page saves through `config_set` / `config_reset`
(comments kept by the backend).

| Entry type | Control |
|---|---|
| `bool` | Toggle |
| `int`, `float` | number box; with `min` and `max`: slider + number box |
| `enum` | Select over `options` |
| `duration` | text, checked against `^(\d+(\.\d+)?(ms\|s\|m\|h\|d))+$` (`"30s"`, `"5m"`, `"1h30m"`) |
| `list<T>` | values with remove buttons + an add box (items parsed as T) |
| `string` | text |

Words, not badges: Restart (requiresRestart), Synced (sent to Kiln clients; also on sections), Pending. A changed row
gets a gold diamond before its label and shows the default with Reset; an invalid row shows an iron-red message under
the field. Sections come from `schema.sections` (entry path prefix), headed in small caps at 18.

## Motion
Sheet up 660 ms `cubic-bezier(.2,1,.25,1)`, down 460 ms ease-in; nav diamond and tab line 500 ms
`cubic-bezier(.3,1.32,.4,1)`; load-order rows glide 520 ms keyed by mod id; a lit flame pops in 300 ms and flickers on
a 1.9 s loop; the arch rises from its base in 520 ms; an imported row arrives flushed with ember (2 s). With
`prefers-reduced-motion` everything is instant and nothing flickers.
