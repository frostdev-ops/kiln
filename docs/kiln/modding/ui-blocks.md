# UI as blocks: windows, HUD lines and keys

A settings window, a status line on the HUD, a key that opens the window: three kinds of block, in C# and in `blocks/*.json`, with the same
shape. They bind to your mod's own config and to a few read-only values of the game. No draw code, no threads, no input handling.

```csharp
Config.Register<MyConfig>(ConfigKind.Client);                       // first: a block names the config's entries

var settings = Blocks.Define("settings", new WindowBlock
{
    Title = "My mod", Width = 360,
    Rows =
    [
        new ToggleRow { Label = "Show the clock", Bind = "config.clock.show" },
        new SliderRow { Label = "Volume", Bind = "config.audio.volume" },       // the range of the config entry
        new ButtonRow { Label = "Close", Does = new UiAction { CloseWindow = "settings" } },
    ],
});
Blocks.Define("clock", new HudLineBlock { Text = "{game.clock}  {game.fps:0} fps", Anchor = HudAnchor.TopRight, When = "game.inGame && config.clock.show" });
Blocks.Define("open-settings", new KeyBindingBlock { Key = "F8", Does = new UiAction { OpenWindow = settings } });
```
```jsonc
{ "format": 1,
  "windows": {
    "settings": { "title": "My mod", "width": 360, "rows": [
      { "type": "toggle", "label": "Show the clock", "bind": "config.clock.show" },
      { "type": "slider", "label": "Volume", "bind": "config.audio.volume" },
      { "type": "button", "label": "Close", "does": { "closeWindow": "settings" } } ] } },
  "hudLines": { "clock": { "text": "{game.clock}  {game.fps:0} fps", "anchor": "topRight", "when": "game.inGame && config.clock.show" } },
  "keyBindings": { "open-settings": { "key": "F8", "does": { "openWindow": "settings" } } } }
```

Every field is in [schemas/kiln-blocks.md](schemas/kiln-blocks.md#window). A sample with both forms: `mods/samples/UiBlocks`. A template:
`dotnet new kiln-ui -n MyUi --blocks true` (the default copy draws in C#, [../ui.md](../ui.md)).

**What players need.** The mod, on their game: `clients: mod`. A window, a line and a key are drawn and bound by each player's game from your
file, and the server cannot send them. The build works `clients` out; a project that says `none` or `kiln` gets an error naming the block.
**On a dedicated server** the blocks are checked and kept (`blocks: <mod>: ui: window settings kept, nothing drawn or bound here` in `kiln.log`), so a
`side: both` mod runs there with no errors, and a mistake is a mistake on both sides. Nothing is drawn, bound or read.

## Values

A value is a name. Three places take one: a row's `bind`, a `{name}` inside a text, and a condition.

| Name | Means |
|---|---|
| `config.<path>` | An entry of one of **this mod's** configs, by its dotted path: `config.clock.show`. Two configs with the same path: `config.<config id>/<path>` (`config.my-mod-client/clock.show`) |
| `game.fps` | Frames per second the game presents, measured over at least half a second |
| `game.frameMs` | Milliseconds the overlay's last frame took on the CPU |
| `game.state` | `Menu`, `Hosting` (this game hosts the world), `Remote` (it joined one), `DedicatedServer` |
| `game.inGame` | true while playing, hosting or joined |
| `game.screen` | The game screen that is open: `InGame`, `PauseMenu`, `MainMenu`, `Inventory`, `Map`, `Crafting`, ... ([../ui.md](../ui.md), `ScreenKind`) |
| `game.language` | The game's text language: `en-us`, `de-de`, ... |
| `game.clock` | The computer's time, `HH:mm` |

Those are all the game values. Each is safe to read from anywhere and costs at most one native read. The player's health, mana or position are
not among them: they live in the game's world, and a block reads nothing there.

Kiln checks every name when the block compiles: `game.fpz` is refused with the list of game values, `config.clock.nope` with the entries the config
has, a config that does not exist yet with "call `Config.Register<T>()` before `Blocks.Define`".

**Templates.** `{game.fps:0.0}` shows a value; `{{` and `}}` are braces. A number takes a .NET custom format (`0`, `0.00`, `#,##0`); a
true/false takes two words, `{game.inGame:in game|menu}`; a text takes none. A text row, a separator's heading, a tooltip and a HUD line are
templates. Labels, titles and button texts are plain: braces in them are braces.

**Conditions** (`when`): a name (true, not 0, not empty), `!name`, or `name op value` with `==`, `!=`, `<`, `<=`, `>`, `>=` (the last four on
numbers), joined with `&&`. There is no `||`: use two rows or two lines.
`"game.inGame"`, `"!game.inGame"`, `"game.screen == InGame"`, `"game.fps < 30 && config.hud.show"`. A text compares without case.

**Texts in languages.** Any text can be `{ "en": "Save", "de": "Speichern", "*": "Save" }`, in C# `UiText.Of(("en", "Save"), ("de", "Speichern"))`. The
game's language picks: the exact key (`pt-br`), `pt`, `en`, `*`. A plain string is the same text in every language.

## Windows (`windows`)

A window or a screen of rows. It exists from the moment the mod loads, closed. A key, a button, a menu entry or `openAtStart` opens it.

| Field | Means |
|---|---|
| `title` | required |
| `kind` | `window` (default): an overlay window the player drags; the X closes it. `screen`: a full screen in the game's look, the game behind it dims, Escape closes it, a gamepad drives it |
| `width`, `height` | pixels at UI scale 1. 0: a window fits its rows; a screen is 720 x 520 |
| `position` | window: `[x, y]`, the window's centre as 0..1 of the screen |
| `autoResize`, `titleBar`, `alpha` | window: grows with its rows; the title bar (default true); background opacity 0..1 |
| `interactive` | window: `false` is a panel the player only looks at: the game keeps the mouse and keyboard |
| `dim` | screen: darken the game behind it (default true) |
| `openAtStart` | open as soon as the game has it |
| `refresh` | seconds between updates of what the rows show, 0.1 to 3600. Default 0.25 |
| `menu` | `{ "text": "...", "in": "pause" / "main" / "both", "order": 30 }`: an entry in the game's own menu that opens it (lower `order` first; Kiln's Mods entry has 1000) |
| `rows` | at least one, at most 200 |

A field for the other kind (`position` on a screen, `dim` on a window) is an error, not ignored.

| Row (`type`) | Shows | Fields besides `when`, `sameLine`, `tooltip` |
|---|---|---|
| `text` | a line of text, a template | `text`, `color`, `wrap` (not both: the overlay's wrapped text has no colour) |
| `value` | `Label: value`, not editable | `label`, `bind`, `format` |
| `toggle` | a checkbox on a true/false config entry | `label`, `bind` |
| `slider` | a slider on a whole-number or decimal config entry | `label`, `bind`, `min`, `max` (default: the entry's `[Range]`; without one, give both; not wider than the range, except past a soft side, `SoftMin` / `SoftMax = true`) |
| `choice` | a drop-down on an enum config entry | `label`, `bind` |
| `separator` | a line, with a heading when it has `text` | `text` |
| `button` | a button | `label`, `does` |

`when` hides the row while it is false (checked at each refresh). `sameLine` puts it beside the row before. `tooltip` shows while the pointer rests
on the widget.

**Editing a config.** A toggle, slider or choice shows your change at once and writes the config on the next tick: a toggle and a choice now, a
slider once it has rested for 0.4 s (a drag is a change every frame, and a write is a file). The write is `ConfigFile.TrySet`, so the config's own
rules apply (range, `[RequiresRestart]`, `Changed` events). A refusal is a toast and a line in `kiln.log`. A **server** config is read-only while
this game has joined a server, and so is any entry that holds the server's value (`[Synced]`): the row shows the value as text.

## Actions (`does`)

A button's `does` and a key's `does` are the same record: **exactly one** of these.

| Field | Does | Where it runs |
|---|---|---|
| `command` | runs a command line of this game's command registry, as the console folder would: as the host's own character when this game hosts the world, else as the console | the tick, in this game |
| `toggle` | flips a true/false config entry: `"config.clock.show"` | the tick |
| `set` | `{ "bind": "config.audio.volume", "to": 50 }`: a value the entry takes (a number in its range or past a soft side of it, an option of an enum, true/false). Checked when the block compiles | the tick |
| `castAbility` | asks the **server** to cast an ability block of this mod (`"other-mod:name"` for another's), as its key would: its costs and cooldown apply, and a refusal is a toast | a message to the server |
| `applyEffect` | applies an effect block, or an effect written in place, to the player, through a hidden ability `ui-apply.<name>` Kiln makes for it (one per effect) | a message to the server |
| `openWindow`, `closeWindow` | opens or closes a window block of this mod (or `"other-mod:name"`'s) | this game |
| `toggleWindow` (since 0.2.0-dev.31, mod API 0.2.17) | opens the window when it is closed, closes it when it is open; a key fires while a mod's own window has the input (not while one of its text fields has the keyboard), so the same key closes it; `"whileCapturing": true` only adds firing over the Kiln menu | this game |
| `toast` | a short message in the corner of the screen | this game |

Limits worth knowing:
- **`command` does not reach a server this game joined.** Chat commands a player types are the server's; a client has no call that sends one. A command
  here runs in this game's own registry: the mod's own commands, or Kiln's on a game that hosts. For what the server must do, use `castAbility` or `applyEffect`.
- **`castAbility` and `applyEffect` need the same mod on the server** (the ability is registered there) and a server with Kiln. Otherwise the toast says the server does not have the ability.
- At most 64 commands wait for the tick; more are refused with a toast.
- `ui-apply.*` names are reserved for the hidden abilities. An authored block or a different effect using the same name is refused.

Windows may open each other. A name that is no block yet is looked at when the mod's pass is over (about 12 ticks), and a wrong one is then an error naming
the block and field.

## HUD lines (`hudLines`)

A line of text on the game's screen.

| Field | Means |
|---|---|
| `text` | required; a template |
| `anchor` | `topLeft` (default), `topCenter`, `topRight`, `middleLeft`, `center`, `middleRight`, `bottomLeft`, `bottomCenter`, `bottomRight` |
| `color` | `#rrggbb`, `#rrggbbaa`, or `white`, `black`, `gray`, `red`, `green`, `yellow`, `orange`, `cyan`. Default white |
| `when` | shown only while it holds |
| `refresh` | seconds between updates, 0.1 to 3600. Default 1 |

A line whose condition is false, or whose text is empty, is **not on the screen at all**, so the overlay has nothing to draw for it.

## Key bindings (`keyBindings`)

| Field | Means |
|---|---|
| `key` | required; the default: `F8`, `Ctrl+K`, `Shift+Alt+F2`, `Numpad5`, a mouse input (dev.27+: `Shift+Mouse4`, `Ctrl+WheelUp`, [ui.md](../ui.md#key-bindings)), or `None` to bind it once the player chooses |
| `description`, `category` | what the Keys tab says; the group (default: the mod's name) |
| `does` | required; an action |
| `onGameScreens`, `whileCapturing` | as `KeyBinding`: a key that types fires in play only, unless true; also fire while the Kiln menu is open |
| `was` | the name this block had before a rename |

The id is `<mod>:<name>`, so the player's choice in the Kiln menu's Keys tab, kept in `kiln/config/keybinds.json`, survives a restart. Rename the
block and say `was`, or the rebinding is lost.

## Richer windows (since 0.2.0-dev.31)

Mod API 0.2.17. Window blocks also have text fields, tables, tabs, trees and pictures, a typed state of their own, a mod's own data,
live values of the player's game, and buttons that run a command on the joined server. `mods/samples/UiBlocks`'s
`blocks/camp.json` uses every one of them (Ctrl+F9, or "Camp" in the pause menu).

### New rows

| `type` | What | Fields |
|---|---|---|
| `input` | A text field on a text config entry or a string state entry. What is typed is a draft: Enter writes it (with `commit: "blur"`, the default, also leaving the field); Escape puts the old text back. Only in an interactive window or a screen. | `id`, `label`, `bind`, `maxBytes` (2..4096 UTF-8 bytes, default 256), `commit` (`blur`, `enter`), `multiline` |
| `table` | The rows of `world.local.inventory` or of a mod's own rows source, in its order, told apart by key. A cell is a template over the row's fields: `{row.count}`. With `selected` a click selects a row. | `id`, `items`, `columns` (1..16 of `{id, label, cell}`), `maxRows` (1..512, default 128; more rows show a notice, never a cut list), `selected` |
| `tabs` | Tabs with their own rows; the first visible tab at first. A hidden or removed selected tab moves to the first visible one. | `id`, `tabs` (1..16 of `{id, label, rows, when}`), `selected` |
| `tree` | Nodes of a mod's own tree source (`nodes`) or written in the block (`roots`, with `fields`). Under an open branch and each leaf, `nodeRows` (up to 8 text or value rows) show `{node.<field>}`. | `id`, `nodes` or `roots`, `fields`, `nodeRows`, `selected`, `expanded` (a stringSet entry) |
| `image` | A PNG of the mod's folder. | `id`, `asset` (relative, no `..`, no URL), `width`, `height` (0: the picture's own), `does` |

New rows need an `id` (`[a-z][a-z0-9_-]{0,63}`, not `r` followed by digits: the older rows use those), unique in the window with nested rows counted. Limits: 200 rows in
all, 8 levels of nesting, 16 columns, 16 tabs, 512 table rows or tree nodes. Every limit, a repeated id and a wrong name is
a compile error naming the field.

### A window's state

`state` declares typed entries the window's rows bind as `state.<key>`: `bool`, `int64`, `decimal` (`scale` 0..6,
never rounded), `string` (`maxBytes`), `stringSet` (what a tree's `expanded` holds). In JSON int64 and decimal
numbers are strings (`"initial": "9007199254740993"`), so no tool rounds them. State is kept while the player stays in
one world (closing and opening the window keeps it), starts again in the next world and is never saved: a config is the
place for what must last. A toggle, a slider (int64 within the slider's range, decimal within ±1,000,000), a text field
and `toggle` / `set` actions write it. At most 64 entries and 256 KiB per window, 2 MiB per mod. Every write keeps the
entry's declaration: a tabs row's `selected` entry must hold its longest tab id (a compile error otherwise), and a widget's
write past `maxBytes`, `maxItems` or a range (a long table row key, a third expanded branch with `maxItems: 2`) is refused,
with one warning in the log.

From C#: `BlockUi.State(window)` reads one of the mod's own windows at one version, `BlockUi.SetState(window, key, value,
expected)` writes one entry. Writes commit in order at Kiln's next service tick (the server's thread on a host); a
changed write advances the revision once and posts `UiWindowStateChangedEvent` (a tab bar or tree that keeps its own
state: `UiControlStateChangedEvent`); writing the value an entry has is accepted and changes nothing. A new world posts
`UiWindowStateResetEvent` with a new session token, and writes asked for before it are refused `StaleSession`. These
events, and `UiBindingValueChangedEvent`, come on the game's main thread in the client world's next step (at once in a
menu, where there is no client world), so a handler may touch the client's own state.

### A mod's own data

`BlockUi.Bindings.Define(id, schema)` declares a scalar, rows or a tree source with typed fields (at most 16; `key`,
and for trees `parent`, `label`, `depth` are built in), before the blocks that bind it as `mod.<id>`. Define it on every
side: a dedicated server has no overlay, but it checks and keeps the windows that bind it. `Publish` needs an overlay
(`ui.blocks.bindings`; guard it with `Ui.Available`).
`BlockUi.Bindings.Publish(id, snapshot)` copies a snapshot at once and commits it at Kiln's next service tick
(`UiBindingValueChangedEvent`); a newer publication before the commit makes the older one `Superseded`. Rows with a
repeated key, and a tree with a repeated key, a missing parent, a loop or more than 8 levels are refused whole: the last
publication stays. 64 sources, 512 rows or nodes and 64 KiB a publication, 256 KiB per mod. A mod reads only its own
sources.

### Values and where they come from

| Name | Type | Comes from |
|---|---|---|
| `state.<key>`, `state.<key>/count` | the declared type; count of a stringSet | the window's state |
| `mod.<id>` | the source's schema | the mod's last committed publication |
| `world.local.available`, `player`, `session`, `step` | bool, whole numbers | this game's client world (they come with any publication; on their own they ask for the vitals group, the cheapest one) |
| `world.local.health`, `healthMax`, `stamina`, `staminaMax`, `mana`, `manaMax` | whole numbers | the values the game itself computed, before any mod's HUD change |
| `world.local.position.x`, `.y`, `.z` | decimal metres (6 places) | the local player's position in this game's world |
| `world.local.inventory`, `inventoryCount` | rows (`key`, `item`, `itemId`, `count`, `pide`, `inventoryPage`, `slotIndex`), a count | the local player's own nonempty slots |
| `world.server.available`, `world.server.hours` | bool, decimal hours | **only when this game hosts the world**; on a joined world the hour is not shown (deferred) |
| `stats.<mod>/<id>` (`/level`, `/revision`, `/available`) | decimal (raw / 10^scale), whole numbers, bool | 05b's synced copy ([mod-values](../services/mod-values.md)); another mod's stat needs `Blocks.ImportStat` or `"statImports"` and its own read policy |
| `row.<field>`, `node.<field>` | the field's type | a table's row (in its cells), a tree's node (in its node rows) |

Numbers are exact: an int64 like 9007199254740993 shows and compares as it is, and a condition compares a name with a
literal of its own type (`state.n > 0.5` on an int64 is a compile error; old `config.*` conditions keep working). A
value that cannot be read now (no world yet, not published, a field the game build does not offer) shows as `—` and
fails every test, `!` and `!=` too; `<name>.available` tests it. Live values are collected by Kiln's core on the right
world's own thread into one immutable snapshot (at most every 100 ms, while an open window or a HUD line's condition asks
for them); a window never reads the game's world itself. `BlockUi.ReadWorld()` and `BlockUi.DemandWorld(fields)` give
mods the same snapshot.

### Commands on the joined server

`"commandTarget": "server"` on a `command` action (or `BlockUi.ExecuteOnServer(line)`) sends the line to the server
this game joined (or hosts) over the optional channel `kiln:ui-command`. The server runs it as the pressing player, whom
it knows from the connection, through the command's own permission and cancel rules: nothing in the message names a
player, an account or an admin flag. The player sees the command's own reply or refusal as a toast. Each request runs at
most once (a repeated id answers from the server's memory and never runs again), and the server limits each player to 4
requests a second, 64 waiting and 2 running; a request that waited 5 s is not run. No answer within 15 s says so: the
command may or may not have run, and Kiln never sends it again by itself. **A server without Kiln (or an older Kiln) has
no channel: the button says "This server cannot run UI commands" and nothing runs, never on this game's own console.**

### Text fields and the keyboard

A text field is only allowed where the window takes the keyboard (an interactive window, a screen). While such a window
is open the overlay keeps key presses and typed characters from the game and its chat; key releases still reach the game,
so no movement key stays held. Tab moves to the next widget, Enter and Escape as above. A text is cut at a character to its
`maxBytes` before anything is written, and never holds a NUL.

## What it costs

- **A closed window costs nothing.** It is not drawn and reads nothing.
- **An open window** issues its widget calls every frame (that is how an immediate-mode overlay draws), with strings and numbers worked out at its
  `refresh`. Values, conditions and templates are read once per refresh, in one pass, not per row and not per frame.
- **A HUD line** is worked out at its own `refresh` by the service tick; the frame only draws the text. No line shown, no window open, no toast: the game's presents pass through untouched.
- One registered tick handler does the HUD lines, the queued commands and the config writes. It exists once a mod has a UI block, and does a clock read and a look at each line's due time when nothing is due.

Unloading removes its windows, menu entries, HUD lines and keys. Pending commands and config edits are dropped. A callback already copied by a frame or key event checks the original mod lifetime, so it cannot act after a reload.

## When something is wrong

Every mistake is named by file, block and field, when the mod loads (and `kiln lint` says most of them before):

```
blocks/ui.json: block window settings, rows[2].bind: config.my-mod/volume is not a true/false config entry: a toggle edits one (a value row shows any value)
block hudline clock, refresh: 0 is outside 0.1 to 3600 seconds: a window or a line updates at a pace, never every frame
block window settings, rows[4].does: an action does one thing, and this has command and toast: use one button or key for each
block keybinding open, key: unknown key 'Hyper+9' (examples: F8, Ctrl+K, Shift+Insert, Numpad5, None)
```

The other blocks of the file still load. KILN110/111/112 in C#: a window name that is no block, an ability or effect name that is none, a mod that says
`clients: none`. `kiln blocks show <name>` says what a block became: the rows with their bindings, the actions with their targets.

## What it does not do

Cut on purpose, because Kiln.Ui cannot do it yet or it would not be a block:
- **No free drawing** in a window. That is `Ui.Window` and `Ui.Screen` in C# ([../ui.md](../ui.md)), and a block window can sit beside one.
- **No values of a joined server's world** (its time of day, authoritative inventory or vitals): `world.server.*` is for the world this game hosts. A mod's stats reach a joined client through [mod values](../services/mod-values.md).
- **State is never saved,** and `mod.<id>` is the mod's own: another mod's data is not readable (stats are, by their read policy).
- **No `||` in a condition, and no arithmetic.** Compare a value with a number; do not compute.
- **A window's title is fixed in the language the game had when it opened** (a window is made again when it is next opened in another language); a screen's title follows by itself.
- **The overlay's wrapped text has no colour.**
- **`command` without `commandTarget: "server"` does not reach a joined server** (above); with it, a server without Kiln runs nothing.

## Client checklist

What only a game with the mod can confirm (the unit tests and the dedicated-server lab cannot draw, press a key or click). Use `mods/samples/UiBlocks`.
1. The game's main menu and pause menu have **UI blocks sample** and **UI blocks tour** entries (`menu`). Choosing one opens the window.
2. **F8** opens the settings window in play and in the pause menu; **F9** the tour screen. Escape closes the screen, the X the window.
3. The window shows `Kiln UI: 143 fps, Hosting` and the number follows the game (twice a second). A slider drag changes the number at once; half a second later `kiln/config/ui-blocks-client.json` has the new value.
4. A toggle writes the file at once. **Show the line** off: the HUD line top right disappears within its refresh (half a second).
5. **Pulse** (a button) and **Calm** (a button) on a joined Kiln server that has the mod: the pulse heals, a refusal (cooldown) is a toast; Calm gives mana.
6. Open the Kiln menu's **Keys** tab: `ui-blocks:open-settings` is there under the mod's name, and rebinding it keeps through a restart.
7. Join a server, open **Settings** with a server config bound: the row reads as text, not a widget.
8. Switch the game's language: the menu entries and the screen's title follow; a window's title follows at its next opening.
9. (0.2.0-dev.31, Camp window, Ctrl+F9 opens and closes it) Type non-ASCII text in **Note**, use Backspace, Tab and Enter: nothing reaches the chat; walk (hold W), open Camp with Ctrl+F9 while holding W, let go: the character stops; Escape puts the old text back.
10. Switch tabs, open and close a branch of **Map**, select a quest, see the picture; close and open the window: all kept; leave the world and come back: reset.
11. The local values follow the game (health, position, slots used); **World hour** shows when hosting and says "not shown" on a joined world.
12. **Rest at the fire** on a joined Kiln server with the mod: the server log shows the player as the command's source, the toast says the reply, **Rests at the fire** goes up. On a vanilla server: the "cannot run UI commands" toast and nothing else.

The guided checks for the owner's two-client pass are in the package's CLIENT-CHECKS.md (09-ui-blocks).

The lab (`mods/tests/ui-blocks-test`, a dedicated server) shows the rest: `ui-blocks-test: PASS ...` lines, and `ui-blocks-test done: N PASS, 0 FAIL`.
