# Kiln.Ui: overlay windows, HUD, toasts, key bindings, the Kiln menu

The client-side UI for mods (Forge: Screens, GUI overlays, toasts, KeyMappings, the Mods screen). Native: `kiln.ui`
(`native/core/abi/services/kiln_ui.h`), which draws Dear ImGui 1.93 into the game's Vulkan presents and takes the game
window's input, and hooks the game's own UI (its screens, its main and pause menus, its crafting menus). Managed:
`managed/services/Kiln.Ui`. RE: `docs/re/client-render.md` (overlay, input), `docs/re/game-ui.md` (the game's UI).
Samples: `mods/samples/UiSample` (overlay), `mods/samples/MenuButtons` (game menus, a screen, crafting),
`mods/samples/ValueBars` (values in the game's own UI), `mods/samples/SlotMarks` (marks and clicks on the game's slots). Test:
`mods/tests/ui-test`. On a dedicated server `Ui.Available` is false and every call does nothing (a `side: both` mod may
call it freely).

| Forge | Kiln |
|---|---|
| `ScreenEvent.Init` + `addRenderableWidget` (buttons in the title / pause screen) | `Ui.Menus.Add(MenuLocation.MainMenu / PauseMenu, text, onClick)` (native entries, the game's renderer) |
| `Minecraft.screen`, `ScreenEvent.Opening/Closing` | `Ui.Screens.Current`, `Ui.Screens.Changed`, `ScreenChangedEvent` |
| a `Screen` subclass + `setScreen` | `Ui.Screen(title, draw, options)` → `Open()` / `Close()`; Escape closes |
| container screens (`AbstractContainerScreen`), `ScreenEvent.Render` | `Ui.Crafting.AddPanel`, `Ui.Crafting.AddTab` / `AddSubcategory`, `Ui.Crafting.Current` |
| GUI overlays, toasts, KeyMappings, the Mods screen | `Hud`, `Hud.Toast`, `KeyBindings`, the Kiln menu (Insert, and the game menus' **Mods** entry) |
| `Component.translatable` | `UiText` (per-language texts; the game's language) |

**As data.** A window, a HUD line and a key binding can also be blocks (`WindowBlock`, `HudLineBlock`, `KeyBindingBlock`, or `windows`,
`hudLines`, `keyBindings` in `blocks/*.json`): rows bound to the mod's config and to a few read-only game values, buttons that run a command, set a
value, cast an ability or open a window, with no draw code. They sit on this API (`Ui.Window` / `Ui.Screen`, `Hud`, `KeyBindings`, `Ui.Menus`) and
change nothing in it: [modding/ui-blocks.md](modding/ui-blocks.md). What they cannot do (text fields, tables, tabs, images, free drawing) is what
this page is for.

## The game's own UI
Mods load in the game's menu (docs/kiln/modding/lifecycle.md), so `OnLoad` can add menu entries before any world
exists. Everything here is client only; players without Kiln are unaffected.

### Entries in the main menu and the pause menu
```csharp
var screen = Ui.Screen("My mod", DrawScreen);                       // below
var (main, pause) = Ui.Menus.AddToBoth(UiText.Of(("en", "My mod"), ("de", "Mein Mod")), screen.Open);
var e = Ui.Menus.Add(MenuLocation.PauseMenu, "Teleport home", () => Client.Post(GoHome), order: 5);
e.Attention = true;      // the game's attention marker (pause menu)
e.Visible = false;       // hide for now; e.Text = "…" relabels; e.Dispose() removes it
```
- The entries are drawn **by the game's own menu-entry renderer**, right above the game's Settings entry: the game's
  look, sounds, hover, and its mouse, keyboard and gamepad navigation. Lower `order` first; Kiln's own **Mods** entry
  (opens the Kiln menu) has order 1000 (`Ui.Menus.KilnEntryVisible = false` hides it).
- `onClick` runs on the game's UI thread (the main thread) after the frame in which the player chose the entry, as
  the owning mod. Keep it short; open a screen, toggle something, or `Client.Post` work to the client world.
- Labels follow the game's text language (`UiText`; `GameLanguage.Current` = "de-de", ...): exact key, primary
  language, `en`, then `*`. A plain string is the same text for every language.
- `Ui.Menus.Add` returns null where the menus are not hooked (dedicated server, an unsupported game build).
- Another loader may route the game's Settings entry through its own code (ShroudForge's native runtime does in the
  main menu, also when Kiln hosts it). The entries are drawn before that entry all the same, and `kiln.log` says once
  through which module it is drawn (`ui: the main menu's Settings entry is drawn through ...`).
- The game's menus do not scroll: every entry before Settings pushes the game's own entries down, and with enough of
  them Quit leaves the screen. So the player's Kiln setting `modEntries` (`kiln/config/menus-client.json`, on the
  Mods page under **Kiln settings**) decides when mods' entries are drawn: `Auto` (the default: every one in the pause
  menu while it can scroll, below, wrapped or not; otherwise as `Few`), `Few` (a menu draws them while it has at most
  3 visible ones, else none of them), `Always` (every one, as before 0.2.0-dev.27) or `Never`.
- **The pause menu scrolls (an experiment, feature `ui.menu_scroll`).** When its entries (the game's, Kiln's and the
  mods', from the first to Quit) are taller than the menu's column at this resolution and UI scale, kiln.ui wraps the
  column in the game's own scroll view (the one the credits use: mouse wheel, clipping, following the keyboard and
  gamepad focus); when they fit it draws the menu exactly as the game does. This holds in every `modEntries` mode:
  with SET and Kiln, the game's 13 entries plus Kiln's Mods, Creative and SET tools already put Quit below a 1280x720
  or 1920x1080 screen. The height comes from the game's own layout: Kiln opens and closes empty frames of no size
  before the menu's first entry, right after it (one row's height) and after its last, and decides for the next
  frames (the layout read is two frames old). A bottom frame below the column is not laid out where it is drawn (the
  owner's 1920x1080 dev build read 978 of 1020 with Quit below the screen), so it counts only when it is at least the
  entries' count times a row; else the entries' count times a row decides, and the old count rule may wrap too. The
  menu leans to scrolling: it wraps when the entries are taller than the column minus one row, and unwraps only 12 UI
  units below that (a row is about 60), so it never flips back and forth; two readings that disagree keep it a scroll
  view for the rest of the game. The game's own spacer and build number below Quit do not count. Until a height has
  been read (the first frames of a game, or a game build where it never reads) it scrolls once it has more than 3
  mods' entries, as dev.27 and dev.28 did. Begin and end sit at the column's own
  open and close in the game's builder (`native/services/ui/menu_scroll.h`). A crash guard keeps
  `kiln/state/menu-scroll.trying` on the disk while Kiln's calls in the menu (the height frames, the scroll view) run
  and have not yet drawn 48 frames each in that start (a wrong nesting would have ended the game by then). The frames
  add up over the start's openings of the menu: the marker goes 8 frames after the menu closes, at the end of the frame
  in which the player chose Quit to Desktop, when the game destroys a world in order, and at an exit with code 0, and
  it is written again at the next opening until the 48 are reached. A world stop during a UI frame clears it once
  that frame finishes. A failed deletion is retried every 8 UI frames and at orderly world stops or exit; a new trial
  cancels the pending deletion before its calls. So only a game that ends in (or within a few
  frames of) Kiln's calls leaves it; a start that finds it (or cannot read it) keeps scrolling and the height frames
  off (`kiln/state/menu-scroll.off`, naming the Kiln version and game build, written before the marker goes;
  another version or build tries once more; delete the file to try again) and tells the player with a toast. A fault
  in Kiln's own calls closes what it opened, in order, and turns scrolling off; when even that fails the game ends on
  purpose with a kiln.log line, rather than a moment later in its own menu code. kiln.log says when it is on
  (`ui: the pause menu can scroll`), what it read (`ui: the pause menu's entries: <entries> of <space> UI units`,
  then each change: `... are taller than its space ...: it scrolls`, `... fit its space ...: drawn as the game draws
  it`), the scroll view's sizes (`ui: the pause menu scrolls: ...`) and when it switched itself off and why. The main
  menu does not scroll. Kiln's own **Mods** entry is always drawn. Every mod's entry of the menu the player can open
  where they are (the main menu, or the pause menu in a world) is listed on the Mods page under **Menu entries**;
  choosing one there closes the Kiln menu and runs the entry's `onClick` exactly as the menu would (on the UI thread
  after the next frame). Nothing changes for a mod: `Ui.Menus.Add` returns the entry as before, whether a menu draws
  it or not. `Few`'s 3 is the setting's limit, not a measured fit: the pause menu does not depend on it, and the main
  menu (which does not scroll) can still lose its last entries on a small screen; the guided client check
  `MenuRowsLeaveQuitOnScreen` asks how many entries fit before Quit leaves the screen.

### Kiln's line in the main menu's footer (since 0.2.0-dev.29.11)
At the bottom of the main menu the game draws its footer: the build number and the legal line. Kiln adds its own line
right above the build number: Kiln's mark, then **Kiln &lt;version&gt; - &lt;N&gt; Mods Loaded** ("1 Mod Loaded").
- **The text** is drawn by the game itself (its footer's font, size, colour, alignment and safe-area padding: kiln.ui
  builds the line exactly as the game builds its build-number line). The mark is a picture, which the game's text cannot
  hold: the overlay draws it in the gap at the start of the line, sized to the line, at the place the game laid the line
  out. Without the overlay (`KILN_UI_OVERLAY=0`, the overlay's crash guard) the line has no gap and no mark.
- **The version** is the release version of the runtime (`0.2.0-dev.29.11`), without the `+<date>.<commit>` build
  suffix; a local build shows the version it was built with.
- **N** counts every mod that loaded this start, of every engine Kiln runs, as Kiln's Mods page counts them (its rows
  whose state is Loaded): Kiln mods, native DLL mods, native plugins that are not Kiln's own, chain DLLs, and the mods of
  EMF, EML, XHL, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink and Ember. Kiln's own services
  and plugins are not mods; a mod switched off, waiting, failed or not loaded does not count. Kiln.Ui reads the list
  again every 2 seconds while the title or the main menu shows, so a mod that an engine starts late is counted too.
- **XHL-Core.** XHL-Core 2.1.0 draws its own line at this place: a green "All mods loaded", or a red line about a
  problem ("XHL: Game updated · Click to view", "Mod conflict detected. Click to view") that opens XHL-Core's
  diagnostics when clicked. With XHL-Core in the game, **Kiln's line names it**: "Kiln 0.2.0-dev.29.19 with XHL-Core -
  N Mods Loaded" (key `native.footer.xhl`, in every language Kiln has). XHL-Core's green line is left out (Kiln's line
  says XHL-Core runs); **its red line is drawn under Kiln's** while XHL-Core reports a problem, in XHL-Core's words,
  colour and language, and it opens XHL-Core's diagnostics on a click, with the keyboard or with a pad, as without Kiln.
  Nothing else of XHL-Core changes (its plugins, its Insert menu, its other hooks); it installs as it always did,
  whether it starts before Kiln or after, and Kiln notices its patches when they come late.
  How: Kiln runs its own copy of the game's footer function, made from the exe file at start, whose window call and
  build-number call go through Kiln. They call the game's functions, or, while XHL-Core reports a problem, the targets
  XHL-Core patched into the game's footer, so XHL-Core draws its line and takes its clicks itself. Kiln knows XHL-Core
  by the export `XhlNativeGameBuild` of the module behind the patched call, and reads only its startup state (one
  number, found by the shape of XHL-Core's state getter, not by an address); where that state cannot be found, XHL-Core's
  line is always drawn, green or red, so a warning is never hidden (docs/re/game-ui.md "The main menu's footer").
  kiln.log: `ui: the main menu's footer: XHL-Core's footer line is at ...`, then `... XHL-Core reports a problem` or
  `... reports no problem` whenever that changes.
- **The setting** `mainMenuFooter` (on by default; Kiln's config `kiln/config/menus-client.json`, on the Mods page under
  **Kiln settings**) hides it: the game's footer is then drawn as it always was, with XHL-Core's line (green or red) where
  XHL-Core is installed.
- **Fail safe.** On a game build where the footer's code does not match (feature `ui.menu_footer` off), when the copy
  cannot be made or checked, when another module changed the footer's code in a way other than a patched call, before
  Kiln.Ui has counted the mods, or after a fault in Kiln's line, the game's own footer function runs (with XHL-Core's
  line where it is installed). Each of these is one kiln.log line (`ui: the main menu's footer ...`).
- Native: `KilnUiApi.menu_footer(mods, flags)` ([services/ui.md](services/ui.md)); Kiln.Ui calls it, a native mod need
  not.

### Which screen is open
```csharp
var s = Ui.Screens.Current;           // GameScreen: Kind, State (keen::UiState), Hud (keen::HudScreenType), Crafting, Language
if (s.Kind == ScreenKind.MainMenu) …
Ui.Screens.Changed += (before, now) => Log.Info($"{before.Kind} -> {now.Kind}");   // game UI thread, menus included
Events.On<ScreenChangedEvent>(e => …);                                             // event bus, tick thread, next step
```
`ScreenKind`: None, Title, MainMenu, Loading, Cinematic, Credits, InGame, PauseMenu, Settings, Inventory, Map, Journal,
Character, Crafting, SkillTree, Storage, Dialog, Chat, Other. `GameScreen.StationWorkshop` is the workshop id of the
crafting station the player uses (0 when none). The event observes only: the game's change has already happened (no cancel).
`GameScreen.PopupOpen` (since 0.2.0-dev.31.9, mod API 0.2.19, GitHub issue 117) is true while one of the game's own
popups is open over its UI in the frame the screen is of: the item menu a right-click on an inventory slot opens, a
drop-down, a context menu, or the game's error popup. Kiln's overlay draws above the game's UI; step aside while it is
true if you draw over the game's screens (`Ui.Inventory` marks already do). Opening or closing a popup is not a screen
change: read it from `Ui.Screens.Current`, on `Ui.Screens.UiTick` or in a draw callback. False on a screen read longer
ago and where capability `ui.popups` is off. Read from the game's code, not yet seen in a running game.

### Custom screens
```csharp
var screen = Ui.Screen(UiText.Of(("en", "Waypoints"), ("de", "Wegpunkte")), ui =>
{
    if (ui.BeginChild("list", 240 * ui.Scale, 300 * ui.Scale))
    {
        foreach (var w in _points) if (ui.Selectable(w.Name, w == _sel)) _sel = w;
    }
    ui.EndChild();
    ui.SameLine();
    ui.Slider("Radius", ref _radius, 1, 100);
    ui.InputText("Name", ref _name, 64);
    ui.Combo("Icon", ref _icon, IconNames);
    if (_image != null) ui.Image(_image, 64, 64);                  // Ui.LoadImage("icons/pin.png") (PNG)
    if (ui.Button("Save")) Client.Post(Save);
    if (ui.Button("Close")) ui.CloseScreen();
}, new UiScreenOptions { Style = UiStyle.Game, Width = 640, Height = 480 });
KeyBindings.Register("mymod.waypoints", "F6", screen.Toggle);       // a key opens it too; so can a chat command
```
- While a screen is open it has the mouse and keyboard (the game's input pauses; key and mouse releases still reach
  the game, so nothing stays held), the background darkens (`DimBackground`), and **Escape closes the top screen**
  (`CloseOnEscape`). `Opened` / `Closed` events; `IsOpen`; `Ui.OpenScreens`.
- Looks: `UiStyle.Player` (the default since 0.2.0-dev.29.16: the look the player chose for Kiln's UI, the game's unless
  they changed it), `UiStyle.Game` (the game's menu colours and **the game's own UI font**, which Kiln reads from the game's
  resources at run time and never ships) or `UiStyle.Kiln` (Kiln's ember theme, branding/palette.json), or a mod's own
  theme (`Theme = ...`, [Looks and themes](#looks-and-themes-020-dev2916)). Inside any window:
  `using (ui.Look(UiStyle.Game)) { … }`.
- Widgets: everything in `UiDraw` (below) plus `Image` / `ImageButton` (PNG via `Ui.LoadImage`, or an item's icon via
  `Ui.ItemImage`; `ImageTrimmed` / `ImageButtonTrimmed` without its margin, `ImagePart` for part of it), `ItemIcon`
  ([Item icons and names](#item-icons-and-names); `ItemIconTrimmed` without the icon's margin), `Combo`,
  `InputMultiline`, `Space`, `WindowRect`, `CloseScreen`.
- Open from the game's menus (`Ui.Menus`), a key binding, a command or code (any thread).
- `FullScreen = true`: the whole display, no title bar, not movable or resizable (Width/Height are ignored); draw your own
  title and layout with the calls in "Layout for full screens". Escape still closes it.
- Where it opens: centred by default; `Position`, `MoveTo` / `Resize` and `RememberPlacement` since 0.2.0-dev.31.11
  ([Placing screens and windows](#placing-screens-and-windows-since-020-dev3111-mod-api-0220)).
- Screens are Kiln overlay windows (ImGui), not game widgets. Gamepad: while Kiln has the input the game sees an idle
  pad (XInputGetState hooked) and the pad drives ImGui's navigation (d-pad / left stick move, A activates, LB/RB
  switch tabs, B closes the top screen like Escape). Native menu entries use the game's own pad navigation. Under Proton the mouse works wherever the game leaves the pointer free (main menu, pause menu, crafting
  and inventory menus); see "Windows" below for in-game pointer lock.

### Placing screens and windows (since 0.2.0-dev.31.11, mod API 0.2.20)
A mod can say where its screen or window opens, move and resize it while it is open, and have Kiln remember where the
player put it (GitHub issue 146). The same units as `UiWindowOptions.Position` always had:

- a **position** is a fraction of the viewport, 0..1 on each axis (0 = the left or top edge, 1 = the right or bottom
  edge), and places the window's **centre** unless a pivot names another point of it (0, 0 = its top left corner);
- a **size** is pixels at UI scale 1 (the units of `Width` / `Height`; Kiln multiplies by `ui.Scale`).

| Member | What it does |
|---|---|
| `UiScreenOptions.Position` | where the screen's centre opens; null (the default) = the middle, as before. Applied each time it opens |
| `UiScreen.MoveTo(x, y, pivotX = 0.5f, pivotY = 0.5f)`, `UiWindow.MoveTo(...)` | puts the pivot point at (x, y), once |
| `UiScreen.Resize(width, height)`, `UiWindow.Resize(...)` | sets the size, once; a resize alone keeps the top left corner |
| `UiScreen.Placement`, `UiWindow.Placement` | a `UiPlacement` (`X`, `Y` = the centre, `Width`, `Height`, `Left`, `Top`, `Right`, `Bottom`): where it was drawn last; null before its first frame (always for a `FullScreen` screen), kept while it is closed |
| `UiScreenOptions.RememberPlacement`, `UiWindowOptions.RememberPlacement` | Kiln keeps where the player left it, across opens and game starts |
| `UiScreenOptions.PlacementId`, `UiWindowOptions.PlacementId` | the id it is remembered under, unique among your mod's screens (or windows); null = the title (a screen's in English) |

- **Once, on the next frame.** `MoveTo` and `Resize` are applied on the next frame the screen or window is drawn, with
  ImGui's `Always` condition for that frame only; after it the player can drag and resize it again. Any thread. A later
  call replaces one not yet applied.
- **On a closed screen** (or a hidden window) the call waits and is applied on the first frame after it next opens, so
  `screen.MoveTo(...); screen.Open();` and `screen.Open(); screen.MoveTo(...);` do the same. It is for that open only:
  the next open starts from the remembered placement or the options again (call it in `Opened` to keep it). On a removed
  screen or window, and on a `FullScreen` screen, the calls do nothing (bad values still throw).
- **Precedence.** An explicit `MoveTo` / `Resize` wins over the remembered placement, which wins over the options
  (`Position`, `Width`, `Height`). A remembered placement is applied whenever the screen or window appears; a window's
  options still apply with `FirstUseEver`, as before, while nothing is remembered.
- **Kept on the screen.** Kiln keeps the whole rect inside the current viewport when it places by a position (the option,
  a remembered placement, `MoveTo`; a window whose size is not known yet: its pivot point), and caps a size from
  `Resize` or a remembered placement at the viewport, so a screen saved on a bigger monitor or at another resolution
  cannot open off-screen (`Width` and `Height` themselves are used as given, as before). ImGui's own
  minimum window size and any size constraints still apply on top (they are applied inside `Begin`). Without
  `Position` or a remembered placement a screen opens centred at its size, exactly as before 0.2.0-dev.31.11.
- **Remembered sizes** apply only where the player can resize: a `Resizable` screen, a window that is not `AutoResize`.
  The position applies to every remembering screen and window.
- **Where it is kept.** `ui-placement.json` in Kiln's config folder, beside `keybinds.json` (the active profile's config
  folder when the launcher runs one, else `kiln/config`), per mod id, then `screens` or `windows`, then the id: `x`, `y`
  (the centre, fractions) and `width`, `height` (pixels at UI scale 1). Kiln writes it about a second after a change (at
  most once a second while the player drags a window), and at once when the mod unloads (also when the game quits and
  unloads every mod), as a whole new file put in place; a write that fails is tried again at the next of these. A file that is not JSON is kept aside as
  `ui-placement.json.bad` with a warning in the log, and every screen and window starts from its options; an entry that
  is not a placement (a missing or non-number field, a size of 0 or less) is skipped; a key given twice counts once (the
  last one). A file Kiln cannot read (locked by another program, no permission) is left as it is for that game start,
  with a warning; so is a file a newer Kiln wrote (a higher `version`, after going back from a beta), whose placements
  Kiln still uses as far as it reads them. Delete an entry, or the file, to forget it.

Grimoire (F3) opening beside Better Spells (Shift+F3) when both are loaded:
```csharp
// Better Spells: tells Grimoire where its screen is whenever that changes (Mods.Send works from any thread)
UiPlacement? sent = null;
_screen = Ui.Screen("Better Spells", ui =>
{
    if (_screen!.Placement != sent && Mods.IsLoaded("grimoire")) Mods.Send("grimoire", "betterspells.placement", sent = _screen.Placement);
    DrawSpells(ui);
}, new UiScreenOptions { Resizable = true, RememberPlacement = true });
_screen.Closed += () => Mods.Send("grimoire", "betterspells.placement", sent = null);
KeyBindings.Register("betterspells.open", "Shift+F3", _screen.Toggle);

// Grimoire: opens with its left edge on Better Spells' right edge while that is open, else where the player left it
Context.Messages.On("betterspells.placement", m => _beside = m.Payload as UiPlacement?);
_screen = Ui.Screen("Grimoire", DrawGrimoire, new UiScreenOptions { Resizable = true, RememberPlacement = true });
KeyBindings.Register("grimoire.open", "F3", () =>
{
    if (!_screen.IsOpen && _beside is { } b) _screen.MoveTo(b.Right, b.Y, pivotX: 0);  // this open only
    _screen.Toggle();
});
```
Kiln clamps the move, so a Better Spells screen at the right edge puts Grimoire as far right as still fits. Placement
needs nothing new in kiln.ui (the managed side uses the window position, size and rect slots it already had). Tested
without a game, not yet seen in a running game. In the test kit: `KilnHarness.UiPlacement`
([unit tests](modding/unit-tests.md#ui-placement)). For two windows that stay together, use
[docking](#docking-windows-since-020-dev3116-mod-api-0223) instead: no messages, no lag.

### Docking windows (since 0.2.0-dev.31.16, mod API 0.2.23)
A window can dock to another window, also one of another mod, and Kiln keeps the two together in every frame, on the
render thread (GitHub issue 149, thanks Fuzen777: what Grimoire and Better Spells did with messages, frame counting and
a Shift listener, a frame or two late). Windows (`UiWindow`) only; screens do not dock.

| Member | What it does |
|---|---|
| `UiWindowOptions.AcceptDock` | other windows may dock to this one; default false. Without it a window cannot be found, docked to, read or moved by another mod |
| `Ui.FindWindow(modId, placementId)` | a `UiWindowHandle` for that mod's window with that `PlacementId` (else its title when made) that accepts docks; null when there is none now |
| `UiWindowHandle.Available`, `.Open`, `.Placement`, `.ModId`, `.PlacementId` | read only; a handle is a name, looked up each time (two of the same mod and id are equal, also with `==`) |
| `UiWindow.DockTo(handle, side, offset = 0)` | docks this window on `DockSide.Left` / `Right` / `Top` / `Bottom` of that one, `offset` pixels at UI scale 1 along the shared edge (down for Left and Right, right for Top and Bottom) |
| `UiWindow.Undock()`, `UiWindow.DockedTo` | leaves the dock (stays where it is); the handle it is docked to, or null |
| `UiWindow.DockChanged` | a `UiDockChange` (`DockedTo`, `Side`, `Offset`, `ByPlayer`) whenever the dock changes |
| `UiDraw.WindowMovedByUser`, `UiDraw.WindowResizedByUser` | inside a window's draw: the player holds it to drag it, or holds a resize grip or border, this frame |

- **Together in the same frame.** Kiln draws each group of docked windows from the one that leads it this frame (the one
  the player drags or resizes, else one with a waiting `MoveTo`, else the window the others dock to, unless that one is
  just opening beside a window already on the screen: then that window) and places every other one from its
  neighbour's rect of that same frame. So a drag, a resize of the shared edge, or a `MoveTo` of either
  window moves the other with it, with no lag. Docking only moves windows, it never resizes them.
- **Both ways.** Dragging the docked window moves the window it is docked to as well: the pair moves as one. A `MoveTo`
  of the docked window moves the pair too. That is why the target has to accept docks.
- **Shift.** Holding Shift while dragging one of them, the player moves it alone: it leaves its dock (`DockChanged`
  with `ByPlayer`), and so do windows docked to it. Dropped again within 16 pixels (UI scale 1) of its old side (the
  side it asked for, or the other one it was shown on), while the two overlap along it, it docks again there, at the
  offset it was dropped at (`DockChanged`, `ByPlayer`, with that side). After your own `Undock()`, a drop does not dock
  it again.
- **Kept on the screen.** The group is kept inside the viewport (the player drags it against a wall), and a dock that
  becomes active (the window or its target opens, `DockTo`, a drop) uses the other side when the window that goes
  beside the other does not fit on its own side but fits there (a target at the right edge: the window docked Right
  opens on its left). `DockTo` does not change: the next time the dock becomes active it tries its own side first. A
  group too big for the viewport keeps the window the others dock to inside first, then as many of the windows docked
  to it as still fit: a docked window (a wide one, a far offset) never pushes the window it docks to off the screen.
  While the player holds a resize border the group is not moved (ImGui would grow the window instead); it goes back
  inside when they let go.
- **Opening.** A window docked to an open window appears beside it in its first frame, and a window others dock to
  that opens while one of them is on the screen appears beside that one: a window already shown never jumps. When the
  docked window opens while its target exists but is closed (one key press opening both, its binding first), it is not
  drawn until the target opens, a quarter of a second at most: it never shows at its own place first. With the target
  closed longer, it is drawn where it is, and the target opens beside it.
- **Closed, unloaded, back.** While the target is closed, or its mod is not loaded, the docked window is free and
  `DockedTo` stays: the dock comes back when the target opens again (beside the docked window when that is shown), also
  a reloaded mod's new window with the same `PlacementId` that accepts docks (the handle is the name). A window docked
  to nothing open is drawn exactly as before.
- **Loops.** `DockTo` refuses the window itself (`ArgumentException`) and a target that is docked to it, directly or
  through others (`InvalidOperationException`). A loop that only forms later, as mods come and go, is cut each frame:
  the newer dock is left out. A window docks to one window; several may dock to one, and chains work.
- **Remembering.** A remembering window (`RememberPlacement`) does not remember a place it only got from its dock:
  its size is kept, its position stays the player's own, so opened alone it goes back there. The window the others dock
  to remembers where the group went. Remember the dock itself, if you want to, from `DockChanged`.
- **Threads.** `DockTo`, `Undock`, `DockedTo` and the handle's members work on any thread. `DockChanged` runs as your
  mod, on the calling thread for your own calls and on the render thread for the player's (keep it short).

Grimoire docked to the right of Better Spells; both open with F3:
```csharp
// Better Spells: accepts docks, nothing else
_window = Ui.Window("Better Spells", DrawSpells, new UiWindowOptions { PlacementId = "spells", AcceptDock = true, RememberPlacement = true });
KeyBindings.Register("betterspells.open", "F3", _window.Toggle);

// Grimoire: docks when it opens (Better Spells may load after it, or not at all)
_window = Ui.Window("Grimoire", DrawGrimoire, new UiWindowOptions { PlacementId = "grimoire", RememberPlacement = true });
_window.DockChanged += c => { if (c.ByPlayer) _alone = c.DockedTo is null; };  // Shift-dragged away: leave it alone
KeyBindings.Register("grimoire.open", "F3", () =>
{
    if (!_window.Open && !_alone && _window.DockedTo is null && Ui.FindWindow("betterspells", "spells") is { } spells)
        _window.DockTo(spells, DockSide.Right);
    _window.Toggle();
});
```
The samples `mods/samples/DockBook` and `mods/samples/DockNotes` are two such mods. Native: kiln.ui's `window_state`
(see [C](#c-native-plugins)) tells Kiln.Ui before each window's `Begin` where it is and whether the player drags or
resizes it; with an older kiln-ui.dll the window the others dock to always leads, so dragging a docked window does not
move its target. In the test kit: `KilnHarness.UiPlacement.FrameWindows` and `PlayerDrags`
([unit tests](modding/unit-tests.md#ui-placement)). Tested without a game (Kiln.Ui's code over a model of ImGui's
windows, and ImGui's own drag and resize headless in kiln.ui's host tests), not yet seen in a running game.

### Crafting menus
```csharp
// A tab and a subcategory in the game's own crafting menu (drawn by the game; workshop 0 = the player's own)
Ui.Crafting.AddTab(workshop: 0, UiText.Of(("en", "Frost"), ("de", "Frost")), new[] { frostEdgeRecipe }, iconFromTab: 0);
Ui.Crafting.AddSubcategory(workshop: 0x56d94f8, tab: -1, "Kiln picks", new[] { recipeA, recipeB });
// An info panel next to every open crafting menu (or only some: When = c => c.Workshop == …)
Ui.Crafting.AddPanel("Recipe info", (ui, c) =>
{
    ui.Text(c.Recipe != 0 ? $"recipe 0x{c.Recipe:x8}" : "no recipe selected");
    if (c.Recipe != 0 && ui.Button("Pin")) Pin(c.Recipe);
});
var now = Ui.Crafting.Current;   // CraftingScreen?: AtStation, Workshop (keen::WorkshopId), Recipe (selected), Journal
```
- Tabs and subcategories go into the game's menu configuration through kiln.content (`kiln.craftmenu`): the menu
  draws them like its own and lists the recipes the player has unlocked. They show when the menu next rebuilds its
  lists (open it again). Recipes in a Kiln subcategory are not also placed automatically (kiln.content's new
  recipes go to a matching subcategory otherwise).
- Limits: the game sizes its menu by the largest tab and subcategory counts in its data (15 tabs, 28 subcategories
  per tab on 26404966); an addition beyond them is refused (null, logged). `Ui.Crafting.Describe()` lists Kiln's
  additions as JSON.
- Panels are Kiln overlay windows in the game's look, first placed along the screen's right edge (or `Left` / `Bottom`), stacked; the player can drag them (the spot is kept);
  their buttons take the mouse while it is over the panel, the keyboard stays the game's. They draw on the render
  thread every frame while a crafting menu is open.
- Workshop ids: `Ui.Crafting.Current.Workshop` while a station's menu is open (MenuButtons' panel shows it; the
  Workbench is 0x4da88f73), or the recipes' own workshop (kiln.content). 0 = the player's own crafting menu.
- **Production stations** (since 0.2.0-dev.29.15): a kiln's, smelter's, loom's or seed bed's screen (`HudScreen.FactoryCrafting`)
  is not the game's crafting menu but a transfer screen ([docs/re/game-ui.md](../re/game-ui.md) section 5), so Kiln's tabs and
  subcategories never show there. A panel shows there when its options ask for it: `new CraftingPanelOptions { AtFactories =
  true }` (off by default, so a panel written for crafting menus does not appear at a kiln with nothing to show; with no such
  panel nothing is drawn there at all). `Ui.Crafting.Current` is then a `CraftingScreen` with `IsFactory` true, `AtStation`
  true, `Recipe` 0, `Workshop` the workshop the game keeps for the station in use (its play data, which its crafting code
  reads as the station's; not yet seen in a game for a factory: **it may be 0 or another station's**, so do not key a factory
  panel on it alone), and `Station` the station's entity (the entity the game's screen is about, read every frame; the game's
  replicated ids are the server's, so a mod sends it to its server half, which checks the player is near it before acting).
  `When = c => c.IsFactory` picks those screens among the ones a panel shows on. `Station` is `EntityId.None` on a bench's
  menu, and on a build where the constant `ui/play.target` does not resolve (kiln.log says so once; the panel still shows).

## Values in the game's own UI (dev.28)
`Ui.Values` replaces what the game itself draws for an item and for the local player: the number and bar of the item
detail view, the bar on an inventory slot, the held item's water indicator and the HUD's health, stamina and mana. It
also adds lines for an item to Kiln's **Item details** window and reads the slots the game drew. **Picture only:** the
server keeps the real values (no healing, no spending, no capacity); a player without the mod and every vanilla client
see the game unchanged. Native: in-core `kiln.ui.values` (`native/core/abi/services/kiln_ui_values.h`). Sample:
`mods/samples/ValueBars`. Guided checks: `05a-c1` to `05a-c5` in `mods/tests/client-pass/Dev28ValuesUi.cs`.

| Member | Draws | Current / Max | Edit | Row (guided check) |
|---|---|---|---|---|
| `ItemBar(item, provider)` | detail view number and bar, its label | what the game prints (water: **charges**; at most 2,147,483,647) | fraction, numbers, label | `ui.values.detail` (`05a-c1`) |
| `SlotBar(item, provider)` | the slot bar (only its fraction shows) | raw fuel units | fraction, numbers | `ui.values.slot` (`05a-c1`) |
| `HeldBar(provider)` | the held item's indicator | raw fuel units | numbers, Max above 0 | `ui.values.held` (`05a-c3`) |
| `Hud(HudValue, provider)` | health / stamina / mana pair | the vital's units | numbers, Max above 0 | `ui.values.hud` (`05a-c2`) |
| `TooltipLines(item, provider)`, `OpenTooltipDetails()`, `CloseTooltipDetails()` | lines in Kiln's Item details window | – | up to 16 lines per detail view | `ui.values.tooltip_lines` (`05a-c4-viewer`) |
| `Slots()`, `SubscribeSlots(onFrame)` | (reads) the slots of the last completed UI frame | – | – | `ui.slots.inspect` (`05a-c5`) |
| `GemForgeCandidates()`, `SubscribeGemForgeCandidates(onFrame)` | (reads) the returned GEMS list, including socketed gems; [API and lifetimes](ui-gemforge.md) | – | – | `ui.gemforge.inspect` |

`item` is an `ItemId`, `default` for every item. Every row is off on a server and on a build where its game code did not
resolve. They are on in every game since 0.2.0-dev.29, subject to their dependencies, but none has been confirmed
in a real game by the checks above ([services/caps.md](services/caps.md#the-client-gate-dev28)).

These six rows exist only on a game client, never on a dedicated server. Their hooks install on first use by a mod (including a slot snapshot read), then remain installed for the process. They require the measured client build 26430625 (SHA `5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77`); after a game update they are off until Kiln is updated. Slot collection continues after its last consumer leaves. Providers run synchronously with no enforced time budget: a slow or hanging provider can freeze the game.

**A provider** (`UiBarProvider`) gets the context, the item (`UiItemView`) and the value as the earlier providers left
it, and answers `UiProviderDecision.Pass`, `.Replace(new UiBarEdit(...))` (only its non-null fields change) or
`.Refuse(reason)` (the earlier value stays). Providers run in registration order; the last one that writes a field wins.
Fuel in a `UiItemView` is always raw fuel units; `UnitsPerCharge` is the detail view's divisor. A slot whose water is
0/0 is drawn full by the game and stays full when providers pass. No provider is called for a held or HUD value whose
game maximum is 0, nor without a local player. Five exceptions or invalid edits in a row remove a provider
(`UiValueProviderChangedEvent`, `Faulted`). A label edit (`UiLabelEdit`: Set, Clear, Game) is for the detail view only.

**Threads.** Providers and `SubscribeSlots` callbacks run on the **game client's main thread**, inside the game's UI
frame (detail, slot, lines) or right after its player-UI update (held, HUD): read values a tick cached, never wait,
never call server-world APIs, never keep the arguments. The events (`UiValueChangedEvent`, `UiValuesResetEvent`,
`UiValueProviderChangedEvent`, `UiValueProviderRefusedEvent`, `UiTooltipPanelChangedEvent`) reach mods **on the tick
thread at the next step**, like Kiln's other events, never inside the game's frame. `UiValueChangedEvent` reports what
was shown per presentation identity (coalesced per frame; Removed after two frames without it, held and HUD values after
two player-UI updates; one `UiValuesResetEvent` on a world change instead of a Removed per value); it is not an engine
write and its identity is not an item's lifetime.

**Text is kept for the process.** A label or a tooltip line is copied into a store that is never emptied while the game
runs (the game keeps pointers to drawn text). Each mod may hold 8 MiB of it, at most 64 new texts a second (burst 128),
4096 bytes a text, 64 KiB per call; over a limit the call is refused (the earlier value stays), nothing is evicted.
**Never make a new label every frame**: keep the label fixed and put changing numbers into Current/Max; a tooltip line
that names a number is interned once per distinct value, so keep such values few.

**Item details.** Extra text is not drawn into the game's tooltip (its layout neither wraps nor makes room for added
lines). It is collected while the game draws an item's detail view and shown in Kiln's own **Item details** window: the
pause menu's "Item details" entry or `OpenTooltipDetails()` opens it on the last inspected item, frozen until its
Refresh button (same item only) or close; a world change closes it. It wraps to its own width (words, then letters)
and scrolls by itself. Kiln adds no hotkey; a mod may (the sample uses F8).

**Slots.** `Slots()` copies the slots of the last completed UI frame (refused when older than 1000 ms or when the frame
drew more than 4096 slots): page, slot, item, count, client entity, the outer and inner rectangles in UI units, the
slot's 16 state bytes (byte 0 is only a candidate hover flag until `05a-c5`), and the page's holder with Kiln's lifetime
serial of it. Pixel rectangles stay null until `05a-c5` verified the mapping. A slot rectangle is never inventory
authority: a server decides what a slot key may do.

**Identity.** `UiItemView.ClientEntity` is the stack's client-world entity; `ServerEntity` is always `EntityId.None` in
this version: Kiln does not prove that a client entity is the server's, and a client-local id must never be matched to
server data by its number. A synthetic detail (the game builds one for an item id alone, e.g. a recipe) has flag 16 and
no slot, count or entity: a provider for one item's instance passes there.

Limits: 128 registrations per mod (values, lines, slots and change subscriptions together; one more comes back with
`Ok` false), 16 tooltip lines per detail view (all providers together). Bad arguments throw `ArgumentException`, a row
that is off throws `KilnUnavailableException` naming it, a refusal comes back as a result. Not done: rows in the
inventory header or character sheet, pet bars, values for a chest a player only opened (`InstanceData` Holder sync
covers the holder only). Lines inside the game's own tooltip: `Ui.Tooltip` below.

### Lines and numbers inside the game's item detail view (Ui.Tooltip, equipment wave)
`Ui.Tooltip` draws lines into the game's **own** item detail view, and scales the numbers its stats block draws, on
this client. Native: `tooltip_lines_native` and `tooltip_values` in `kiln_ui_values.h` (since 0.2.0-dev.29.19); the game
code it uses is mapped in [../re/game-ui.md](../re/game-ui.md) and `sigdb/symbols/client.d/equip-tooltips.json`.

```csharp
Ui.Tooltip.AddLines(myItem, (in UiValueContext c, in UiItemView v) =>
    UiTooltipLines.With(new TooltipLine("+12 % fire damage", TooltipColor.Positive),
                        new TooltipLine("Ember set: 2 of 4", TooltipColor.Epic, Small: true)));
Ui.Tooltip.AddLines((in UiValueContext c, in UiItemView v) => UiTooltipLines.With(new TooltipLine("Affix: sharp")),
    TooltipPlace.AfterStats);
Ui.Tooltip.AddValues(myItem, (in UiValueContext c, in UiItemView v) => UiTooltipScale.By(1.25f));
```

| Member | What it does | Row (guided check) |
|---|---|---|
| `AddLines(item, provider, place)`, `AddLines(provider, place)` | lines after the last vanilla line (`TooltipPlace.End`) or between the stats and the description (`AfterStats`), wrapped at the panel's width as the game wraps its description, in a `TooltipColor` (the game's rarity colours, `Positive`, `Negative`, `Muted`), optionally in the smaller detail size; also listed in Kiln's Item details window | needs `ui.values.tooltip_lines`; drawn in the game's view while `ui.tooltip.lines` is on (`equip.c5` to `equip.c8`) |
| `AddValues(item, provider)`, `AddValues(provider)` | the stats block's numbers (the main damage, armour or block number and its other values) multiplied by the providers' factors (finite, 0 to 1000 in all) | `ui.tooltip.values` (`equip.c9`) |
| `InGame` | true while lines are drawn into the game's view | – |

How it behaves:
- **Inside the scrolled content.** A line is the last child of the detail's scrolled content list (or sits right after
  the stats block), so the panel's own layout places it under the vanilla lines and its scrollbar reaches it. Lines are
  built the way the game builds the description: same style and alignment, size "fill the width, fit the height", so
  the game wraps them; Kiln measures nothing itself.
- **Fallback, never half drawn.** Lines also go to Kiln's Item details window. They are only in that window where the
  game's view cannot take them: `ui.tooltip.lines` off or held, a game build Kiln has not measured, a detail screen that
  does not reach these places (the guided check `equip.c8` lists which do), or after a fault while drawing (one
  kiln.log line; native drawing then stays off for the session).
- **Colours.** `Common` .. `Legendary`, `Unique` and `Muted` are the palette entries the game uses for the header's
  rarity colour. `Positive` is the Uncommon colour and `Negative` the text's own colour until `equip.c7` confirms a
  better match; a later Kiln can change what they map to without an API change.
- **Values.** A picture on this client only: the server's numbers do not change and vanilla clients show the game's
  numbers. The enhancement rows of the stats block are not scaled in this version.
- **Budgets and threads** are `Ui.Values`' (above): 16 lines per detail view for all line providers together, text
  kept for the process, providers on the client's main thread inside the game's UI frame, five faults in a row remove
  a provider. A `Ui.Values.TooltipLines` provider stays in Kiln's window only.
- **On for every player** (0.2.0-dev.30: nothing is held; `equip.c5` to `equip.c9` are the guided checks a real game
  confirms). When a row is down (its sigdb feature missing on a game build, or lowered after a fault), `AddLines`
  still works (Item details window) and `AddValues` throws `KilnUnavailableException`.

**The tooltip probe** (for the guided client session, not for players): `"tooltipProbe": true` (or a key name) in
kiln.json, or the environment variable `KILN_TOOLTIP_PROBE=1`, draws a fixed test block through the same path, stepped
through with a key (default F3: off, short, 16 lines, long English, long German, colours, after-stats, values x2), and
writes `tooltip-probe:` lines to kiln.log (each detail's mode and identity, the sites it passed, rectangles, values).

## The game's inventory slots (0.2.0-dev.30)

`Ui.Inventory` (client only) treats the slots the game draws (backpack, the inventory menu, a chest, a crafting station or
factory, an NPC's crafting) as something your mod acts on: marks over a slot, and clicks on a slot with the item's
identity, and (since slice 2) panels beside the backpack and chest screens with the chest's entity. Sample:
`mods/samples/SlotMarks` (both sides: the client asks, its server half checks the item is the sender's, then the client
draws the mark; a "Count this chest" panel beside chests). Native: `kiln.ui.inventory` (`native/core/abi/services/kiln_ui_inventory.h`)
on `kiln.ui.values`' slot frames and kiln.ui's `subscribe_pointer_press`.

```csharp
// Ctrl+click marks an item; the game never sees the press (no pick-up)
Ui.Inventory.OnClick(new SlotClickOptions { Modifiers = SlotModifiers.Ctrl }, click =>
{
    if (click.ItemEntity.Value == 0) return;           // an empty slot, or a plain stack (no item entity)
    SendToMyServer(click.ItemEntity);                  // your mod's own message: the server half checks it, then keeps the mark
    Ui.Inventory.Mark(click.ItemEntity, new SlotMark(new Color(235, 60, 50)));   // the picture, at once
});
// watch a click without taking it from the game
Ui.Inventory.OnClick(new SlotClickOptions { Button = SlotButton.Right, Modifiers = SlotModifiers.Alt, Take = false }, c => Log.Info(c.ToString()));
// a rule per drawn slot (render thread: a lookup, nothing slow)
using var tint = Ui.Inventory.MarkWhen(s => myMarked.Contains(s.ClientEntity) ? new SlotMark(Color.Red, SlotMarkStyle.Fill) : null);
var under = Ui.Inventory.Hovered;                      // the slot under the cursor, or null (for a key binding)
```

| Member | What | Row |
|---|---|---|
| `OnClick(options, handler)` | clicks matching `Button` (Left, Right) and `Modifiers` (exactly: Ctrl alone is not Ctrl+Shift); `Take` (default) keeps the press from the game, its release still reaches it. The handler runs on the client's main thread after the next UI frame. Dispose to stop. Since 0.2.0-dev.30.5 `Pad = KeyChord.Parse("Pad:LS")` (or `"Pad:LB+A"`) makes it a controller press on the `Selected` slot instead: `SlotClick.Button` is `SlotButton.Pad` and `SlotClick.Pad` the chord; `Take` keeps that button from the game until it is let go (the buttons held for a combination still reach it); the handler runs on the client's main thread after the next game UI frame; no selected slot, or a Kiln screen with the input: no click | `ui.slots.click` |
| `Mark(item, mark)`, `Unmark(item)`, `ClearMarks()` | a mark on the item's slot wherever the game draws it (it follows the item: move it and the mark moves); `SlotMark(Color, Style = Border/Fill/Corner, Thickness = 3)` | `ui.slots.inspect` |
| `MarkWhen(rule)` | a mark from a rule asked for each drawn slot (an item mark wins) | `ui.slots.inspect` |
| `Hovered` | the slot under the cursor in the game's latest UI frame (the mouse's only) | `ui.slots.inspect` |
| `Selected` (0.2.0-dev.30.5) | the slot the game's own selection is on (a controller's: the focus of the game's UI navigation, which the d-pad and stick move), on a screen with a pointer, in a frame at most 250 ms old; with a mouse the game may move that focus with the pointer, so it can follow `Hovered` there. Not checked in a game yet: move focus between slots with the d-pad and confirm this follows it | `ui.slots.inspect` |

**The item's identity.** `SlotClick.ItemEntity` (and `VisibleSlot.ClientEntity`) is the item's entity in this game's
world. Only items with an item entity of their own have one: weapons, tools, armour, cans (the items `Items.Read` also
gives an `ItemEntity`). A plain stack (materials, consumables, ammunition: the game keeps it as an item id and a count)
has none, so a click on it says `ItemEntity` 0 with its `Item` and `SlotClick.Slot.Count`; name such a stack by `Holder`,
`SlotClick.Slot.InventoryPage`, `SlotClick.Slot.SlotIndex` and `Item`. For an item the server sent (every item in a player's, a chest's or a
station's inventory) the entity is the server's own id of it: the game creates replicated entities with the server's id
(`docs/re/network.md`, dossier chapter 05a section 6).
Since 0.2.0-dev.30.5 (mod API 0.2.14, GitHub issue 121) Kiln also says so: `VisibleSlot.ServerEntity` (and so
`SlotClick.Slot.ServerEntity`) is that id for every slot of the local player and of the open container (a storage chest,
the chest-style transfer screen, a factory station: the entity the screen is about), so it equals the server's
`InventorySlot.ItemEntity` there; `HolderFlags` bit 8 marks such a holder. It is `EntityId.None` for a plain stack and for
any other holder. `VisibleSlot.Rarity` (an `ItemRarity?`) and `Level` (also on `SlotClick`) are the item's as the game
drew the slot, the values its tooltip shows, captured on the game's main/UI thread and copied to render-thread mark
rules: a `MarkWhen` rule can tell a Legendary staff from an Uncommon one without `Items.Read`. `Rarity` is null for an empty slot.
`Holder`, `VisibleSlot.InventoryPage` and `VisibleSlot.SlotIndex` say where it was drawn. Send the id to your server half and check
there that the item is in the sender's inventory or a container they may use (Kiln.Items) before acting: a click is a wish,
never authority. Kiln Auto Loot's salvage marks work this way.

**Where the press was** (`SlotClick.Pointer`, since 0.2.0-dev.30.3, GitHub issue 107, thanks Fuzen777): the button's
position, in the overlay's pixels, the space `UiDraw` draws in and `ui.Pointer` reports (top left of the game's
picture is 0, 0). Kiln reads the press in the game window's client pixels, which are the overlay's pixels: the overlay
is drawn at the window's size, and Dear ImGui reads its own pointer the same way. It is not in UI units, as
`VisibleSlot.OuterUi` is. A mod that maps slots itself (the coordinates paragraph below) can check a click against its
own picture with it. `null` only for a `SlotClick` a test made with the constructor.

**Where clicks count (fail closed).** Only on screens with a pointer: the in-game menu (inventory tab), the backpack, the
player's crafting, a storage chest, a station, an NPC's crafting, a factory. Never in the play view: the hotbar is drawn
there but a click attacks. Only while Kiln does not have the input (no Kiln menu or screen open) and the cursor is not over
a Kiln panel. The game's latest slot frame must be this session's and at most 250 ms old. Anything missing: no event, and
the game gets the click as without Kiln. A click still waiting for its UI frame when the world is left is dropped.

**Marks** are drawn by Kiln's overlay over the game's slots, on the same screens and on the play view's hotbar, in this
player's game only. Two mods marking one item: one of the marks shows (no layering in this version).

**The game's popups** (since 0.2.0-dev.31.9, GitHub issue 117, thanks Fuzen777). The overlay draws above the game's UI, so
through 0.2.0-dev.31.8 a mark under the game's item menu (right-click an item: Equip, Salvage, Delete) sat on top of the
menu. Kiln now reads the game's popup panels (that menu, drop-downs, context menus: feature `ui.popups`) and leaves out the
mark of every slot a panel covers, for as long as it is open; the other slots keep theirs. While a panel's rectangle is not
known (its first frame) or the game's error popup shows, no mark is drawn at all. On a game build where Kiln cannot read
the popups (`ui.popups` off) marks are drawn as before. A rule (`MarkWhen`) is not asked for a covered slot. Read from the
game's code, not yet seen in a running game.

**Coordinates.** Kiln maps the game's UI units to pixels from the game's own viewport and pixels per unit
(pixel = viewport + unit * pixels per unit), then scales the game's picture (the viewport's size in pixels) to the window
or the overlay when their sizes differ. The canvas size is in UI units (1920x1080 times the UI scale, wider or taller to
fit the window), never pixels: the mapping does not use it. Through 0.2.0-dev.30.2 Kiln scaled by the canvas instead, so
marks, clicks, `Hovered` and docked panels landed at twice the slot's position at 3840x2160 (pixels per unit times
the right place whenever the game's pixels per unit are not 1: above it at 1440p or 4K, below it at 1280x720 or a UI
scale above 1 at 1080p; GitHub issue 107). This mapping is read from the game's code, not yet confirmed in a running
game: the client checks `ui-hooks.c1` (marks on the slots at two resolutions and UI scales) and `ui-hooks.c2` (clicks on
the right slot) do that (`tools/client-checks`). Until they pass both rows say "game test pending".

**Multiplayer and vanilla.** Everything happens in one player's game: the server and other players see nothing unless your
mod sends a message. A vanilla client or server is unaffected.

**C (native plugins).** `kiln.ui.inventory` v1: `subscribe_clicks(owner, &filter, fn, user, &reg)` (`KilnUiSlotClickFilter`
`{size, button 1/3, mods, flags KUI_CLICK_TAKE}`; `fn(const KilnUiSlotClick*)` with the slot record and the screen),
`slot_at(owner, x, y, target_w, target_h, &hit)` (x < 0: the cursor), `mapping(owner, target_w, target_h, &map)` (pixel =
offset + unit * scale), `unsubscribe`. Registrations share kiln.ui.values' budget. kiln.ui's `subscribe_pointer_press` (a
size-guarded tail at byte 1040, after `chat_completion`'s slot at 1032) gives any native plugin the left and right presses
over the game's UI with a take-or-pass answer.

### Panels beside the backpack and chest screens, and the chest's entity (0.2.0-dev.30)

A mod can dock a Kiln panel (Kiln.Ui's own widgets, the player's look, KilnText) beside the game's backpack screen and a
storage chest's screen while they are open, and learns which of them is open with the chest's entity. Sample:
`mods/samples/SlotMarks` (a "Count this chest" button its server half answers). The quick path to a "Salvage" button
next to a chest.

```csharp
using var panel = Ui.Inventory.AddPanel("Auto Loot", (ui, screen) =>
{
    if (screen.Container.Value == 0) { ui.TextDisabled("..."); return; }   // the game names the chest a frame later
    if (ui.Button("Salvage marked")) channel.SendToServer(new SalvageRequest(screen.Container.Value));
}, new InventoryPanelOptions { Screens = InventoryScreens.Chest, Width = 240 });

Ui.Inventory.ScreenChanged += (before, now) => Log.Info($"{before?.Kind} -> {now?.Kind} {now?.Container}");
InventoryScreen? open = Ui.Inventory.Screen;           // Backpack or Chest, with Hud and Container; null: neither
EntityId target = Ui.Screens.Current.Target;           // the same entity on any chest-style screen (chests, factories)
```

| Member | What | Row |
|---|---|---|
| `AddPanel(title, draw, options)` | a panel drawn every overlay frame (render thread) while a screen of `options.Screens` (Backpack, Chest, All) is open and `When` says so, docked beside the slots the game drew: `Side` Right (default; to their left when there is no room), Left, Below; level with the chest's slots on a chest screen. `Width`, `Style`, `Theme`, `Interactive` (its buttons take the mouse while it is over the panel) as for crafting panels. Dispose to remove | `ui.inventory.panels` |
| `Screen` | the open `InventoryScreen(Kind, Hud, Container)` or null (any thread) | `ui.screens` |
| `ScreenChanged` | `(before, now)` on the game's UI thread after the frame in which a backpack or chest screen opened, closed or changed (another chest; the chest's entity named a frame late). `InventoryScreenChangedEvent` on the event bus for the tick thread | `ui.screens` |
| `GameScreen.Target` | the entity a chest-style screen is about: a storage chest's (StorageChest, CommerceProductChest) or a production station's (FactoryCrafting) | `ui.screens` |

**Which screens.** Backpack: the backpack screen, or the in-game menu while it draws the player's slots (not its crafting
tab, map or journal). Chest: a storage chest (HUD StorageChest) and the commerce-product chest that uses the same screen.
Not: stations and factories (use `Ui.Crafting.AddPanel` with `AtFactories`), loot chests, the play view, any other screen.

**The chest's entity.** `InventoryScreen.Container` is the entity the game's chest screen reads the other inventory from
(play data `+0x2c50` on client 26430625; `docs/re/game-ui.md`). For a chest the server sent, that is the server's id of
it, so your server half can find it: `Items.Read(chest)` for its contents, `World.Position(chest)` for where it is. Check
there that it is a container (not a player: `Players.IsPlayer`), near the sender, and one they may use, before acting. A
panel click is a wish, never authority. `EntityId.None` for the frame or two before the game names it; `ScreenChanged`
fires again when it does.

**Fail closed.** The panel is drawn only when Kiln knows where the game drew the slots this frame (the slot frame of
`ui.slots.inspect` is this session's, at most 250 ms old, of the same screen, with a usable viewport). On an unknown screen,
an unmeasured build, an older kiln-core (no `screen_view`) or a frame Kiln cannot read, there is no panel and the game's
screen is as without Kiln. Placement uses the same pixel mapping as marks: game test pending (client checks
`ui-hooks.c5` to `ui-hooks.c7`).

**C (native plugins).** `kiln.ui.inventory` v2 appends `screen_view(owner, target_w, target_h, &view)` (size-guarded at byte
40; the table is 48 bytes): `KilnUiScreenView` with `kind` (`KUI_VIEW_BACKPACK`, `KUI_VIEW_CHEST`), `hud_screen`,
`container`, the local player, `KUI_VIEW_HOLDER_MATCH` (a drawn slot's holder is the container), and the rectangles of all
drawn slots, the player's and the container's in the target's pixels. kiln.ui's `screen_target` now also answers on the
two chest screens (before 0.2.0-dev.30: factories only).

**Not in this version:** a button inside the game's chest screen where Enlarge sits (drawn by the game's own button
renderer), the game's own slot colours (marks are drawn over the slot), the gamepad's focused slot.

## The HUD's ammunition and spell lists (0.2.0-dev.30.6)

`Ui.AmmoList` (client only; since 0.2.0-dev.30.6, mod API 0.2.15; GitHub issue 127, thanks Fuzen777) lets a mod add rows
to the list a bow's arrows or a staff's spells are picked from, or put its rows in place of the game's: a quiver mod shows
the arrows its server keeps for the player, a spell-book mod the spells it grants. Native: `kiln.ui.ammo`
(`native/core/abi/services/kiln_ui_ammo.h`, served by kiln-tools); capability rows `ui.ammo.list` (`Provide`) and
`ui.ammo.pick` (`Picked`).

```csharp
// after the game's rows: the arrows the server keeps in this player's quiver (the mod's own data)
using var quiver = Ui.AmmoList.Provide(AmmoListKind.Arrow, ctx =>
    myQuiver.Select(q => new AmmoListRow(q.Item, q.Count)).ToList());
myQuiverChanged += () => quiver.Refresh();             // Kiln cannot see the mod's own data change: ask again

// the player (or the game's automatic selection) picked a quiver row the backpack does not hold: the game did nothing
Ui.AmmoList.Picked += pick => SendToMyServer(pick.Item); // the server half checks it, then swaps the stack in

// in place of the game's spells, for both lists at once (one call per list; ctx.Kind says which)
Ui.AmmoList.Provide(AmmoListKind.Arrow | AmmoListKind.Spell, ctx => MyRows(ctx.Kind, ctx.Held, ctx.GameRows),
    new AmmoListOptions { Mode = AmmoListMode.Replace });
```

- **When the rows are asked for.** The provider runs as your mod on the game's UI thread, never inside the game's own
  code: once when the list is first built, then whenever its `AmmoListContext` changes (the held weapon `Held`, the
  game's own rows or counts `GameRows`, as the game last built them, the session) or `Refresh()` is called. Kiln copies the rows and hands the game an
  immutable snapshot, so the HUD may show the previous context's rows for one frame. Keep it a lookup.
- **Order and composition.** Rows show in the order returned. All providers of a list are composed in registration
  order; the first row of an item wins, item 0 is left out, a negative count shows as 0. A list holds 64 rows in all:
  in `Append` the mods' rows fit in what the game's rows leave (none when the game shows 64 itself); in `Replace` the
  mods have all 64. `ctx.Truncated` says Kiln cut rows at 64 (the game's own list was longer, so `GameRows` are its first
  64, or the list shown last was longer).
  `Append` (the default) shows the game's own row for an item the game already lists (with its real count) and none
  of yours in its place; since 0.2.0-dev.31.12 your row of that item is still published behind it, taking no room, so
  a pick of it reaches you once its stack has left the backpack (GitHub issue 145, below). `Replace` shows only
  the mods' rows, and one provider asking for it replaces the whole list. A provider that throws adds no rows that time;
  rows the game refuses (an item that is not an arrow, or not a spell, for that list) drop that provider's rows only.
  Both go to the mod's log.
- **Selection.** The game keeps its selection on the same item when the rows change.
- **A new build of the list in `Replace`.** When the game builds the list again (a pick swapped a stack, a shot used
  one), Kiln asks the providers at the next UI frame. Until it publishes their rows for the new build, a `Replace` list
  keeps showing the rows it showed last, with the selection on them, as if they were made for the new build, rather
  than the game's own rows and the game's selection (the equipped spell); picks of them are decided against the game's
  new rows, as below. Before 0.2.0-dev.31.21 the game's rows flashed for a frame or two in a hosted world, and on a
  dedicated server, where a swap comes back a round trip later, the cursor went back to the equipped spell (GitHub issue
  151, thanks Fuzen777). The rows stay up for at most a second; then the game's own rows show until the next
  publication, and `kiln.log` says so once (`ui.ammo: the spell list shows the game's own rows: ...`). Nothing stays
  over another weapon, player, client session or world, or once the list is cleared. An `Append` list shows the game's
  own rows in that frame, as before (its rows go in around the game's).
- **Picks.** A row whose item the game lists itself with stock (in `Replace` too) or the player carries is the game's
  own: it equips it as always. The row the game keeps at count 0 for a stack the player used up is not stock: since
  0.2.0-dev.30.7 a mod row of that item raises `Picked` unless the player carries it, in either mode (GitHub issue 131,
  thanks Fuzen777). The game's rows are the ones it built last: when the stack of an item your rows provide leaves the
  backpack before the game builds the list again (your server half just swapped it into storage), a pick of its row
  raises `Picked` too, rather than going to the game, which would find no stack and empty the slot. The same holds in
  the frame or two between the game's new build and Kiln's next publication of your rows, when the game's own rows
  show (`Append`) or your last rows stay up (`Replace`) (since 0.2.0-dev.31.10, GitHub issue 145, thanks Fuzen777). In `Append` (also
  `PlayerStorage.Client.ProvideAmmo`'s default) this holds since 0.2.0-dev.31.12 for a row of yours that the game's
  own row of the same item hid while the backpack held its stack: list the item while it is in the backpack too
  (a spellbook's equipped spell), and its pick after your swap reaches you. A row you list only once your own data
  shows the item again (your server's swap reached the game) is yours once the game has built its rows without the
  stack; a pick before that goes to the game, as before. With `"log_level": "debug"` in `kiln.json`, every
  pick of a provided row that goes to the game logs one line (at most 8 a second; in `Append` since 0.2.0-dev.31.12
  also a pick of your row behind the game's own), e.g. `ui.ammo: a pick of spell
  0x1a2b3c4d (a row a mod provided) went to the game: game row count 1 (list revision 221, built 16 ms, 0 client steps
  before the pick); backpack: holds it`. Any other mod row raises
  `Picked` for the mod that provided it (on the UI thread at the next UI frame; an earlier session's picks are dropped),
  and the game does nothing. `Picked` is a selection wish, not proof of a click: the game's own automatic selection (the
  weapon's current ammunition is not in a replaced list, so it selects the first or the kept row) raises it too. Kiln
  never moves an item; your server half validates the wish (the player owns the item, the weapon takes it) and ignores
  repeats.
- **The list shows again.** When the player cycles to a mod row or selects one directly, the HUD shows the list again
  for its usual time, as for the game's own rows (since 0.2.0-dev.31.12, GitHub issue 140, thanks Fuzen777). Nothing
  to call: Kiln does what the game does when the host answers its own selection, for the list the HUD shows (a spell
  row's pick while the bow's arrows show does not open the spell list), and the list hides again by itself. The game's
  automatic selection does not show it, as before. On a game build where Kiln cannot find this, picks work as before
  and `kiln.log` says once that mod rows do not show the list again.
- **Refusals.** `Provide` throws `KilnUnavailableException` when `ui.ammo.list` is off (a server, a game build Kiln has
  not measured, an older kiln-tools); subscribing to `Picked` throws it when `ui.ammo.pick` is off. `Ui.AmmoList.Available`
  and `PicksAvailable` ask first, `Capabilities.Why(Ui.AmmoList.ListCapability)` says why. Disposing the last provider of
  a list, or unloading the mod, shows the game's own list again.
- **A quiver from player storage.** Since 0.2.0-dev.30.7, a mod whose arrows or spells live in a Kiln player storage
  needs none of the above: `PlayerStorage.Client.ProvideAmmo(fullKey, AmmoKinds.Arrow | AmmoKinds.Spell)` provides the
  storage's rows from Kiln's client copy and its server half checks and equips a pick
  ([player storage](services/player-storage.md#the-client-copy-and-the-huds-ammunition-lists)).
- **Unit tests.** The test kit's `KilnHarness.AmmoList` plays the game's side (`Hold` a bow or staff with the game's
  rows, `SetGameRows`, `NewSession`, `KnownItems`), shows what the HUD would (`Shown`, `Provided`, `ModeOf`, `Selected`)
  and picks a row (`Pick`, raised at the next `Step`).

## Outlines on entities in the world (0.2.0-dev.30.3)

`Ui.World.Outline(entity, options)` (client only, any thread) lights an entity's model with the game's own highlight:
a chest that just received items, the place of a searched item. Options: colour, pulse, priority, duration; the
returned `WorldOutline` reads its state and is disposed to end it. Two colours show at once, the game's highlight wins
unless the player's kiln.json says `"ui": {"outlines": "mods-first"}`, and there is no through-walls outline. Members,
states, limits and the C table: [render.md](render.md#outlines-since-020-dev303). Native: kiln.render v2
(`kiln_render.h`), capability `ui.world_outline`.

## Windows (immediate mode)
```csharp
var w = Ui.Window("My mod", ui =>
{
    ui.Text("hello");
    ui.Checkbox("Enabled", ref _enabled);
    ui.Slider("Range", ref _range, 1, 100);
    ui.InputText("Name", ref _name, 64);
    if (ui.Button("Apply")) Client.Post(() => Apply());   // act on the client world from the main thread
    if (ui.BeginTable("t", "Item", "Count")) { foreach (var i in _items) ui.Row(i.Name, i.Count.ToString()); ui.EndTable(); }
}, new UiWindowOptions { Width = 400, Height = 300, StartOpen = false });
w.Toggle();   // Show / Hide / Open = true; the X button closes it too
```
- `UiDraw` exposes:
  - text: `Text`, `Text(color, …)`, `TextWrapped`, `TextDisabled`, `Separator(text)`, `Tooltip`;
  - widgets: `Button`, `SmallButton`, `Checkbox`, `Slider` (float/int), `InputText`, `Selectable`, `Progress`;
    `ItemClicked(UiMouseButton.Right)` right after any widget answers a right click on it (`Button` answers the left one).
    `InputText` / `InputMultiline` edit the whole value when its UTF-8 fits `maxBytes`; a value that does not fit (or
    holds a NUL) is shown cut and is never changed (`InputText` shows it read-only);
  - layout: `CollapsingHeader`, `TreeNode`/`TreePop`, `SameLine`, `Spacing`, `Id(...)` scopes, tables
    (`BeginTable`, `Row`, `NextRow`/`NextColumn`), tabs (`BeginTabBar`/`BeginTab`), `BeginChild`;
  - since 0.2.0-dev.31, mod API 0.2.17 (kiln.ui.blocks, what the richer
    [UI blocks](modding/ui-blocks.md#richer-windows-since-020-dev31) draw with): `BeginControlledTab(label, select)` (a
    tab code selects), `SetNextItemOpen(open)` (the next `TreeNode` opened or closed from code), `TextField(label, ref value, maxBytes, UiTextFieldFlags)` (`EnterReturns`, `ReadOnly`,
    `Multiline`), and `LastItemState` right after a widget (`Active`, `Edited`, `Deactivated`, `DeactivatedAfterEdit`: a
    field that lost the keyboard after an edit). `HasBlockWidgets` says whether this Kiln has them; without, they fall
    back to `BeginTab`, nothing, `InputText` and `None`;
  - free drawing: `DrawText`, `DrawRect`, `MeasureText`;
  - the mouse (since 0.2.0-dev.30.3): `Pointer`, `ButtonHeld(UiMouseButton)`, `DragSource(kind, payload)` /
    `DropTarget(kind)` (see "The pointer, held buttons and drag and drop" below);
  - frame info: `Width`/`Height`/`Scale`/`DeltaSeconds`/`MenuOpen`/`Capturing`.
- **Interactive** windows (the default) take the mouse and keyboard while open: the game stops seeing input, the
  cursor is free, and ImGui draws it. `Interactive = false` gives a panel the player only looks at.
- Under Proton the mouse works fully wherever the game leaves the pointer free (main menu, pause menu). During play the game
  holds the pointer, and under XWayland that is a pointer lock: only relative motion arrives. A real mouse works there: Kiln moves
  its cursor from the motion (seen: 1014 moves and 10 clicks in a window opened by a key during play). An absolute
  pointer (a test driver) doesn't move anything through that lock; Kiln shows the system cursor for one message when capture
  starts, to end it. If a driver still gets nothing, open the screen from the pause menu.
- Threads: the draw callback runs on the game's **render thread** every frame while the window is open. Keep it short.
  Read state the mod keeps (copies, snapshots). Act through `Client.Post` (client world) or `Context.Scheduler.Post`
  (server world, when hosting).
- `Width`, `Height` and `Position` place a window the first time it shows (ImGui's `FirstUseEver`); since
  0.2.0-dev.31.11 `MoveTo` / `Resize` move it later and `RememberPlacement` keeps where the player left it
  ([Placing screens and windows](#placing-screens-and-windows-since-020-dev3111-mod-api-0220)).
- A window belongs to its mod and goes away when the mod unloads. A draw that throws 5 times closes the window.

### Layout for full screens
For pages laid out like the game's own menus (the Kiln menu uses these). Sizes are pixels; multiply your own by `ui.Scale`.
```csharp
using (ui.FontSize(36)) ui.Text("Waypoints");                         // a title: 36 px at scale 1, the game's font kept
if (ui.BeginChild("list", 320 * ui.Scale, 0, UiChildFlags.NavFlattened))  // columns the d-pad can cross
{
    foreach (var w in _points)
    {
        if (ui.Selectable("##" + w.Id, w == _sel, 0, 48 * ui.Scale)) _sel = w;   // a tall row
        var it = ui.LastItem;                                          // its rectangle, hovered/focused/...
        ui.DrawText(UiLayer.Window, it.MinX + 12, it.MinY + 8, w.Name, Color.White, 18 * ui.Scale);
    }
}
ui.EndChild();
using (ui.PushColor(UiColorSlot.Text, Color.Yellow)) ui.TextWrapped("Hint");
if (ui.KeyPressed(UiKey.Tab, claim: true)) NextTab();                  // claim: ImGui's own focus does not move too
```
- `FontSize(px)` (scope), `BeginChild(id, w, h, UiChildFlags, UiWindowFlags)` (no border unless `Border`), `NextItemWidth`,
  `Cursor` (position, space left, window rectangle, font size) and `SetCursor(x, y)`, `LastItem`, `Selectable(label,
  selected, width, height, flags)`, `FocusKeyboardHere()` (type into the next text field), `DefaultFocus()`,
  `DrawRect` / `DrawText` on a `UiLayer` (the current window, behind or over everything), `MeasureText(text, size, wrap)`,
  `PushColor(UiColorSlot, color)` / `PushVar(UiStyleVar, x, y)` (scopes), `KeyPressed(UiKey, repeat, claim)`, `HeldMods`,
  `ScrollHere(ratio)`.
- `ui.HeldMods` is the `KeyMods` (Ctrl, Shift, Alt) Dear ImGui's io has this frame, from the game window's key messages,
  whether or not Kiln has the input; a focus loss (alt-tab) clears it, so a release lost to alt-tab never leaves Shift
  held. For a Shift+drag or Shift+wheel there is no need for a `KeyInput` listener of your own.
  `ui.WindowMovedByUser` / `ui.WindowResizedByUser` (since 0.2.0-dev.31.16, mod API 0.2.23) say the player drags or resizes the
  current window this frame ([docking](#docking-windows-since-020-dev3116-mod-api-0223)).
- `ui.HasLayout` is false on an older kiln.ui: each call then falls back to the older one or does nothing.

### The pointer, held buttons and drag and drop (since 0.2.0-dev.30.3)
GitHub issue 113 (thanks Fuzen777): a two-pane item screen with drag and drop between the panes, or a "hold the button on
a cell" action.
```csharp
// where the pointer is and what is held: enough for a drag of your own
if (ui.Pointer is { } p && ui.ButtonHeld(UiMouseButton.Left))
    ui.DrawRect(p.X - 3, p.Y - 3, p.X + 3, p.Y + 3, Color.White);

// Dear ImGui's drag and drop: a source after one widget, a target after another
foreach (var s in _backpack)
{
    ui.Selectable(s.Name + "##b" + s.Slot);
    ui.DragSource("stack", s.Slot.ToString(), s.Name);       // while dragged: "Iron Bar" by the pointer
}
ui.Selectable("Chest");
if (ui.DropTarget("stack") is { } slot) Client.Post(() => MoveToChest(int.Parse(slot)));   // once, on the release
```
| Member | What |
|---|---|
| `Pointer` | the pointer now, a `Vector2` in the overlay's pixels (`DrawRect`'s space, `SlotClick.Pointer`'s); null until the overlay has seen the cursor. Whole pixels |
| `ButtonHeld(button)` | the button is down now (pressed this frame or earlier, not released), wherever the pointer is. `ItemClicked` answers a press on a widget instead |
| `DragSource(kind, payload, preview)` | after a widget, every frame: the widget can be dragged; true on the frames it is. `payload` is text, at most 4096 bytes of UTF-8 (an id, a slot, JSON); `preview` is drawn by the pointer (null: nothing). Also after a widget without an id (text, an image) |
| `DropTarget(kind)` | after a widget, every frame: the payload's text on the frame a source of that kind is released over it, else null. A matching payload hovering it highlights the widget |

- **Kinds are your mod's own.** Kiln passes Dear ImGui a hash of your mod's id and the kind, so another mod's target named
  `"stack"` never gets your payload, nor you theirs. Any text works as a kind.
- What they read is Dear ImGui's io for this frame: the window's mouse messages Kiln saw, also while Kiln does not have
  the input (the game gets those presses too). In the play view the game recentres the cursor for mouse look, so the
  pointer means something only while a pointer is shown: a Kiln screen or window, the Kiln menu, or one of the game's
  menus. A drag works inside interactive Kiln windows (they have the mouse); a panel the player only looks at
  (`Interactive = false`) gets no presses.
- A drag ends on the release: over a target of its kind the target gets the payload, over anything else nothing happens.
  If the source stops being drawn while the button is held (a list that scrolled or changed), the drag goes on with the
  payload of the last frame it was drawn, and Dear ImGui draws "..." by the pointer instead of the preview.
- On a kiln.ui older than 0.2.0-dev.30.3: `Pointer` null, `ButtonHeld` false, `DragSource` false, `DropTarget` null.
- C: kiln.ui's `pointer`, `drag_source` and `drop_target` (a size-guarded tail at byte 1056, after `chat_search`;
  [services/ui.md](services/ui.md#the-pointer-and-drag-and-drop-020-dev303)).

## Looks and themes (0.2.0-dev.29.16)
Kiln's own UI (the Kiln menu with its Mods, Network, Keys and Overlay sections, the creative menu, the developer windows
and console, Item details, the guided checks, talent trees, block windows, crafting panels, HUD lines, toasts, the radial
wheel and the native fallback window) is drawn in **the player's look**. It is the game's look unless the player chose
another. Mods can give their own windows a theme, and can offer a theme the player may pick for all of Kiln's UI.

### Looks
| `UiStyle` | What it is |
|---|---|
| `Player` (new, the default of `Ui.Window`, `Ui.Screen` and crafting panels) | the look the player chose in Kiln's setting `ui.look` (below): the game's (default), translucent, Kiln's ember, or a theme a mod offers |
| `Game` | the game's look: its menu colours and its UI font, both read from the game's data at run time and never shipped |
| `Kiln` | Kiln's classic ember theme (branding/palette.json) |

- **The game's colours** come from the game's UI bundle (`keen::FbUiBundle`: `menu.tabLabelHighlightColor`,
  `tabLabelIdleColor`, `tileBackgroundColor`, `basicWidgetColor`, `tabHighlightColor`, `tileHighlightOutlineColor`,
  `textTabRowBgHighlightColor`, `globalColors.attentionColor`, `warningColor`, `checkmarkGreen`), whose field offsets come
  from the game's reflection (`kiln.types`) and whose bytes come from `kiln.assets`, on a worker thread the first time a
  look needs them (with the game's font; asked again a few times, 5 s apart, while the game has not listed them yet). Until they are read, and wherever they cannot be (no `kiln.types` or
  `kiln.assets`, a build whose bundle lacks a field, a dedicated server), the game look uses a **neutral dark stand-in**
  close to the game's (greys with a soft gold accent), never the ember red. kiln.log says which (`ui: the game's UI colours
  ...`). The same holds for the font: the system font until the game's is read.
- Each look is a **palette of roles** (`UiThemeRole`): `Text`, `TextMuted`, `WindowBackground`, `PanelBackground`,
  `FrameBackground`, `Border`, `Accent`, `Button`, `Header`, `Attention`, `Warning`, `Ok`. ImGui's colours are made from it
  by one recipe per base (the game's square tiles, Kiln's rounded ember), so a theme that changes `Accent` also changes
  check marks, slider grabs, the chosen tab's line and the focus outline. `ui.Palette` gives the colours of the look in use
  where it is asked (`ui.Palette.Accent`), so a mod's own drawing (`DrawRect`, `DrawText`) can match any look; Kiln's
  pages draw with it.

### The player's setting
In `kiln/kiln.json` (the launcher's **Settings**, "Kiln's in-game look"), and in game on Kiln's menu, **Mods > Kiln
settings > Look**, where a change shows at once and is written back to kiln.json half a second after the last change
(a value edit that keeps the file's comments and layout, on a worker). Kiln.Ui also reads the section again when the file
changes (checked every 2 seconds), so a change in the launcher shows while the game runs.
```jsonc
"ui": {
  "look": "game",       // game | translucent | kiln | theme
  "theme": "",          // with look "theme": "<mod id>:<theme>", a theme a mod offers (Kiln's menu lists them)
  "background": 0.55,   // the translucent look's window background opacity, 0.1 to 1
  "opacity": 1,         // Kiln's whole overlay, text included, 0.3 to 1
  "scale": 1,           // the overlay's size on top of the display's scale, 0.75 to 1.5
  "language": "auto",   // Kiln's own texts: auto (the game's text language) or a tag, "zh-CN" (Kiln's language, below)
  "item_icons": {       // the item icon cache (since 0.2.0-dev.31; "Item icons" below)
    "max_icons": 512,   // icons held at once, 64 to 512
    "max_mib": 48       // their pixels in MiB (RGBA8 textures on the GPU), 8 to 128
  }
}
```
- `translucent` is the game's look with the windows' backgrounds (windows, title bars, panels, the Kiln menu's backdrop
  and the dimming behind screens) at `background`; pop-ups (drop-downs, tooltips) stay at least 85 % opaque.
- `opacity` is ImGui's global alpha for every Kiln window, and the alpha of `DrawRect` / `DrawText` in them, the HUD
  lines and toasts. kiln.render's drawing (`RenderFrameEvent`) is a mod's picture of the world: it is not changed.
- `scale` multiplies the UI scale every Kiln window and `ui.Scale` use (and so the stand-alone Kiln menu, which also stays
  within the display). The Kiln page inside the game's main menu keeps the game's own scale.
- With `look: "theme"` and no such theme offered (its mod is not loaded, or not yet), the game look is used; the setting
  stays, and the theme shows once its mod offers it.
- Only the top-level `ui` section is read (not a `client` section). A missing section or key is the default above; a value
  out of range is clamped, an unknown `look` is `game` (kiln.log says so).

### Kiln's language (0.2.0-dev.29.16)
Kiln's own texts (every page, window, toast and message of Kiln above, and the Mods entry and footer line kiln-ui.dll adds to
the game's menus) come from `runtime/locales/<language>.json`, shipped as `<game>/kiln/locales/`, in the format of a mod's
locales. `ui.language` picks the language: `auto` follows the game's text language, a tag forces one; in game **Mods > Kiln
settings > Language**, under Look, changes it at once. For Chinese, Japanese and Korean the overlay loads the game's own font
of that language group (never shipped); Kiln's texts are English until it is loaded, and stay English if it cannot be.
Mods' own texts are theirs (`Mod.Text`, `UiText`) and follow the game's language. For translators and Kiln's developers:
[translating-game.md](translating-game.md).

**Following Kiln's language** (`KilnLanguage`, Kiln.Api, since 0.2.0-dev.29.19). A mod whose pages sit beside Kiln's and
should read in the same language (a settings panel in the Kiln menu's style, a shared core for several mods) asks Kiln
instead of the game:
```csharp
var lang = KilnLanguage.Current;           // "zh-cn", "en-us": ui.language's tag, or the game's while it is "auto"
var text = Context.Locale.GetIn(KilnLanguage.OverlayInEnglish ? "en" : lang, "panel.title");
KilnLanguage.Changed += () => Relabel();   // another language, the English fallback switched, or a reload
```
- `Current` is a lower-case tag; `Setting` is `ui.language` as set (`"auto"` or the tag). On a dedicated server both
  follow its game language (`"en-us"`).
- `OverlayInEnglish` is true while Kiln's overlay shows its texts in English because it cannot draw `Current`'s script
  yet (the game's Chinese, Japanese, Korean or Cyrillic font is loading, or could not be loaded). Text a mod draws in the
  overlay in that language shows as boxes meanwhile; follow Kiln and show English too.
- `Changed` runs on the thread that made the change (often the overlay's render thread, or the window thread for the
  in-game setting), after the new values are in place. Keep it short. A handler that throws is logged under its mod and
  does not stop the others; a mod's handlers are dropped when it unloads.

### Mod themes
```csharp
var frost = Ui.Themes.Define("frost", new UiThemeSpec
{
    Name = UiText.Of(("en", "Frost"), ("de", "Frost")),   // in Kiln's Look list
    Base = UiStyle.Game,                                    // or UiStyle.Kiln: what it starts from
    Roles = { [UiThemeRole.Accent] = Color.FromRgba(0x7fd4ffff), [UiThemeRole.WindowBackground] = Color.FromRgba(0x0e141cff) },
    Colors = { [UiColorSlot.Border] = Color.FromRgba(0x7fd4ff66) },   // single colours, after the roles
    Background = 0.85f,                                     // the windows' background opacity (null: the base's)
    Rounding = 6,                                           // every corner, pixels at scale 1 (null: the base's)
    Vars = { [UiStyleVar.ItemSpacing] = new(10, 8) },       // spacings, pixels at scale 1
    Font = Ui.LoadFont("fonts/Frost.ttf"),                  // its windows' text (null: the base's font)
    OfferToPlayer = true,                                   // listed in Kiln's Look setting as "Frost (My mod)"
});
var w = Ui.Window("Frost tools", Draw, new UiWindowOptions { Theme = frost });
var s = Ui.Screen("Frost", Draw, new UiScreenOptions { Theme = frost });
Ui.Crafting.AddPanel("Frost", DrawPanel, new CraftingPanelOptions { Theme = frost });
using (ui.Theme(frost)) { /* windows begun here */ }
```
- **Scope.** A theme applies only where its mod puts it: the mod's own windows, screens and crafting panels (`Theme` in
  their options), and `ui.Theme(theme)` inside its own draw callbacks. After every mod callback Kiln takes off any look the
  callback left on, so a forgotten `Dispose` never reaches another mod's window or Kiln's. **No call sets the player's
  look**: `OfferToPlayer` only lists the theme; the player picks it (Kiln's menu or kiln.json `ui.look: "theme"`,
  `ui.theme: "<mod id>:<name>"`). Kiln's own UI always uses `UiStyle.Player`.
- `Ui.Themes.Define(name, spec)`: `name` is 1-64 of `a-z 0-9 . _ -`; the theme's `Id` is `<mod id>:<name>`. Defining the
  same name again from the same mod changes the theme in place (the same `UiTheme`; windows using it follow). Owned by the
  calling mod: gone when it unloads (a window using it falls back to the player's look; a player who picked it sees the game
  look until it is offered again). `UiTheme.Dispose()` removes it earlier. Any thread. On a dedicated server, or with
  drawing off, `Define` returns a theme that draws nothing and is never offered.
- `Ui.Themes.Offered`: the themes mods offer to the player now. `Ui.Themes.Player`: the player's setting (read only).
- Roles take colours without alpha (the alpha is ignored; `Background` is the windows' opacity). `Colors` are
  `UiColorSlot`s with alpha, set after the roles. `UiColorSlot` gains `TitleBg`, `TitleBgActive`, `PopupBg`, `Tab`,
  `TabHovered`, `TabSelected`, `TableHeaderBg`, `TextSelectedBg` (also for `ui.PushColor`).
- The player's `opacity` and `scale` apply to themed windows too: they are the player's.

### Fonts
`Ui.LoadFont(byte[] ttf)` / `Ui.LoadFont(path)` (relative to the mod's folder) loads a TrueType or OpenType font (TTF or
OTF, CFF outlines too) for the mod's own windows: a theme's `Font`, or `using (ui.ModFont(font)) { ... }` around some text.
`UiFontFile` is owned by the calling mod (freed when it unloads, or by `Dispose`). It is added to the overlay's font atlas
at the next frame; until then text keeps the current font.
- **Budget** (`native/services/ui/font_budget.h`): every font shares ImGui's one font atlas, whose glyphs are made on
  demand at the sizes drawn, so a font takes **no** overlay image (the 960-image budget is untouched; the atlas's own
  textures are in ImGui's reserve of 64 descriptor sets). Kiln lets **8** mod fonts exist at once, at most **8 MiB** each and
  **24 MiB** together (the bytes stay in memory while the font lives); past that `LoadFont` returns null (kiln.log says
  why). A file that is not a TrueType/OpenType font (its first four bytes) is refused.
- The game's own font stays the game look's; `UiFont.Game` / `UiFont.GameBold` still pick it in any look.

### C (native plugins)
Appended after `image_state` (the version stays 7; check `header.size` through the member and its pointer):
`theme_create(owner, &KilnUiThemeDef, &handle)`, `theme_free(handle)`, `push_theme(handle)` (inside a frame; popped by
`pop_style`), `set_look(&KilnUiLook)` (Kiln.Ui only, from the player's setting: a native plugin's call is refused with
`KILN_ERR_UNAVAILABLE`), `look_info(&KilnUiLookInfo)` (the player's look and the palette in use), `font_load(owner, data,
len)`, `font_free(font)`, `push_font_handle(font)` (popped by `pop_font`); `push_style(KILN_UI_STYLE_PLAYER)`; roles
`KILN_UI_ROLE_*`; looks `KILN_UI_LOOK_*`. Details: [services/ui.md](services/ui.md#looks-themes-and-fonts-020-dev2916).

## Item icons and names
The game's own icon of an item, in any Kiln window, screen or crafting panel (since 0.2.0-dev.29.15), and its name in the
player's language:
```csharp
ui.ItemIcon(item, 48 * ui.Scale);                                   // the icon, 48 pixels square (0: 64 x Scale)
ui.SameLine();
ui.Text(Items.DisplayName(item) ?? "?");                            // "Holzscheite" in a German game (Kiln.Items)
var img = Ui.ItemImage(item);                                       // an image object: Image, ImageButton, RenderDraw.Image
if (img != null && ui.ImageButton("pick", img, 40 * ui.Scale, 40 * ui.Scale)) Pick(item);
```
- **What is drawn**: the picture the game's inventory and crafting menus show for the item. A game item's icon is the
  `keen::UiTextureResource` under the item's own GUID (every one of the game's 4,268 items has one: BC7, mostly 256x256 with 9
  mip levels). A new item of kiln.content shows its own PNG icon once the game shows it, its base item's (or its `icon`
  item's) before, as the game's own menus do. A retexture of an icon (assets.json) shows too. An id that is no item, and an
  icon that is still loading, draw a neutral placeholder of the same size: the layout never jumps.
- **Read at run time** from the game's resources (kiln.assets' `texture_rgba` reads that texture's pixels and decodes one
  mip level to RGBA; kiln.ui uploads it as an overlay image). Kiln ships no game art and writes the pixels nowhere.
- **Cost**: nothing until a mod draws an icon. The first draw of an item at a size loads it on a worker thread (most take
  well under a millisecond; the first frames show the placeholder). Kiln keeps the level that fits the size drawn (32, 64,
  128 or 256 pixels; a picture with no level that size, such as a mod's one-level PNG icon, is scaled down to it), by default
  at most 512 icons and 48 MiB (since 0.2.0-dev.31; 256 and 24 MiB before): past that the icons not drawn for a second are
  dropped first and read again when drawn; while every icon in the cache is drawn now, a new one waits on the placeholder
  without being read. The player sets the limits in kiln.json, `"ui": {"item_icons": {"max_icons": 64..512, "max_mib":
  8..128}}` (read again while the game runs). The MiB are the icons' pixels, which is what their textures take on the GPU (one
  level, RGBA8, a 64-pixel icon 16 KiB, a 256-pixel one 256 KiB): not the overlay's whole memory, and no frame-time budget.
  kiln.ui lets 960 overlay images exist at once: the icons, the placeholder and up to 128 icons freed in the last quarter
  second (kiln.ui destroys a freed texture at its next frame) stay under 641, so at least 319 are left for mods' own
  pictures (`Ui.LoadImage`) at any setting; while 128 were freed in the last quarter second, a new icon waits a moment rather
  than push another out. Measured: a grid of 300 icons on screen at 64 pixels (or 40 trimmed) is held whole at the defaults
  (256 held only part of it); 40-pixel trimmed icons at a UI scale of 2 read the 256 level (256 KiB each), where 48 MiB holds
  192 of them: a mod with large trimmed grids at 4K may want `max_mib` 64 or more. An icon scrolled out of its window is
  neither read nor drawn; its `ImageButton` keeps the size it has when shown (the frame padding included, since 0.2.0-dev.31). An icon shows once the overlay has uploaded it (a frame or two; at most 8 new pictures a frame); one
  the overlay could not upload stays the placeholder and is read again later. Whatever fails is tried again later, less often
  each time. A new client world (another server) drops them all. `Ui.ItemImage` returns one shared object per item: keep it
  or ask each frame; its `Dispose` does nothing.
- **Sides**: a game client whose overlay draws. On a dedicated server, and with drawing off (`KILN_UI_OVERLAY=0`, the
  overlay's crash guard), `Ui.ItemImage` is null and `ItemIcon` draws nothing; `Ui.ItemIconsAvailable` says whether icons
  draw here. Works in the main menu, in a hosted world and on a client that joined another's world: the icons come from the
  player's own game data.
- **Names**: `Items.DisplayName(item)` is the item's name in the game's text language, read from the game's own texts (also on
  a joined client and in the main menu, where `Items.Get` has no catalog); a new item's definition gives its own (its
  `Texts` for the language, else `DisplayName`). The first call reads the game's texts in the background (a fraction of a
  second): until then, and on a dedicated server (whose data has no texts), it answers the English name Kiln ships. Stations
  and recipes: `Stations.WorkshopName`, `Stations.RecipeName` ([services/stations.md](services/stations.md)).
- Not square (a few lore items: 360x388): the picture keeps its shape inside the square.
- **Trimmed** (since 0.2.0-dev.29.16): the game's icons sit small in the middle of a wide transparent margin, so at 32-40
  pixels the art fills only part of the box. The `...Trimmed` calls draw only the art, as large as fits:
  ```csharp
  ui.ItemIconTrimmed(item, 40 * ui.Scale);                          // the art fills 40 pixels square, centred
  ui.ImageTrimmed(img, 40 * ui.Scale, 40 * ui.Scale);               // the same through Ui.ItemImage (or your own PNG)
  if (ui.ImageButtonTrimmed("pick", img, 40 * ui.Scale, 40 * ui.Scale)) Pick(item);
  draw.ImageTrimmed(img, x0, y0, x1, y1);                           // RenderDraw (HUD layer)
  ```
  The art's bounds (pixels with alpha 16 or more) are found once, when the icon is loaded; the art keeps its aspect ratio
  and is centred, and the box taken stays the size asked for (the layout is the same as untrimmed). A trimmed icon reads a
  sharper level, so the magnified art stays crisp: the first time twice the size drawn (40 pixels reads the 128 level), and
  once a level of that item was read (since 0.2.0-dev.31) the level at which its art's longer side has the pixels drawn: art
  that fills 3/4 of its icon drawn 40 pixels wide reads the 64 level (not a larger one), art that fills 1/4 reads the 256
  level (not a stretched 128). The placeholder
  fills the box. `ItemIcon`, `Image` and `ImageButton` draw exactly as before. (Own names, not overloads: a method group
  released in dev.26 gains no overload.)
- **Part of an image** (since 0.2.0-dev.29.16): `ui.ImagePart(img, width, height, u0, v0, u1, v1)` and
  `draw.ImagePart(img, x0, y0, x1, y1, u0, v0, u1, v1)` draw the rectangle (u0, v0)-(u1, v1) of the picture, in 0..1 across it
  (a sprite sheet's cell; u1 < u0 flips it).
- Not done: icons of buffs, skills or anything that is not an item; the item's rarity frame or count (draw them yourself);
  the icon of an item on a server (no pictures there).

### Client steps (0.2.0-dev.29.15; none of this has run in a real game yet)
Run headless already: on a dedicated-server lab (server 26430253) the same mod's server half passed 8 of 8 (`iconcheck: PASS
...`): no image and `ItemIconsAvailable` false there, `Items.DisplayName` from the shipped English names, a new item's name
from its definition, `Stations.WorkshopName(0x2546ae49)` = "Charcoal Kiln" and `RecipeName` from the server's own
registries, and empty answers for a base no one has. The decoders ran on the client's real Charcoal icon offline (its 128
level, the right picture). Everything drawn, the game's texts in a language and the factory screen need a game client.

With `mods/tests/item-icons-check` deployed (`dotnet build mods/tests/item-icons-check/ItemIconsCheck.csproj -t:KilnDeploy
-p:KilnModsDir=<game>/kiln/mods`), in a hosted world (lines `iconcheck:` in kiln.log):
1. F9 (or the pause menu's **Item icons check**): the window shows the icons of Wood Logs, Charcoal, the Bronze Sword, the
   Powder Ball, the Charcoal Kiln and a Wizard chest at 32, 64 and 128 pixels, as the inventory shows them (colours right,
   not blurred at 128, no black squares); the map piece keeps its tall shape; `Unknown 0xdeadbeef` is the grey placeholder;
   `New item (this mod)` shows the Wood Logs' icon. The first opening may show placeholders for a frame or two.
2. The names next to them are in the game's language (switch the game to German: `Holzscheite`; the new item:
   `Symbolprüfscheit`); kiln.log's `iconcheck: names (de-de): ...` agrees. Hover an icon: its tooltip names it.
3. Open a workbench: the **Icon check** panel shows `AtStation True, IsFactory False` and the Workbench's workshop.
4. Open the charcoal kiln (a production station): the panel shows (it never did before; it sets `AtFactories`), with `IsFactory True`, `Station`
   the kiln's entity (in the owner's base the server's id 12205; compare with `/stationcheck show` of stations-client-check),
   and `Workshop` 0x2546ae49 (the kiln's) or 0/another station's (record which: the field is not proven for factories).
5. Join a Kiln server as a client (or leave and host again): the icons and names still draw; the panel at a kiln still shows.

## Radial wheel
A wheel in the middle of the screen: 1 to 64 entries, 8 per page. It is what EMF's `wheel_open` draws. From any thread:
```csharp
var wheel = Ui.OpenWheel(["Wolf", "Bear (level 3)", "Fox"],
    pick => Log.Info(pick < 0 ? "cancelled" : $"picked {pick}"),          // render thread, as your mod
    new UiWheelOptions { Title = "Pets", HoldKey = UiWheel.HoldShift });
int choice = await wheel!.Result;                                           // or await it: -1 = cancelled
```
- While it is open Kiln has the mouse and keyboard, as for an interactive window: the pointer picks the highlighted
  sector (nothing in the middle), the camera does not move, keys do not reach the game (releases do).
- A left click on the highlighted entry chooses it. Esc or a right click cancels. The mouse wheel turns the page.
- `HoldKey` (a generic key code: `UiWheel.HoldShift`, `HoldCtrl`, `HoldAlt`, or a letter): the player holds it while the
  wheel is open, and letting go chooses the highlighted entry (in the middle: cancels). A tap (let go within 300 ms with
  nothing highlighted) leaves the wheel open for a click. A key that is not held as the wheel opens counts as none.
  No mouse buttons, no left/right-specific codes (they throw `ArgumentException`).
- "Name (details)" shows the details on a second line. Long labels are shortened; the highlighted entry shows in full
  under the wheel.
- One wheel at a time, and not while the Kiln menu is open: `OpenWheel` returns null then (and on a dedicated server).
  `UiWheel.Close()` / `Dispose()` ends it without calling `pick`; a mod that unloads loses its wheel.
- `UiWheel.Result` resumes on the context that awaited it, so `await` from tick-thread code stays on the tick thread.
- Not done: gamepad steering. The pad is idle for the game while the wheel is open.

## HUD and toasts
```csharp
Hud.Show("mymod.status", () => $"temp {_temp:F1}", HudAnchor.TopRight, Color.Orange);  // text computed per frame
var line = Hud.Show("mymod.msg", "static text");  line.Text = "changed";  line.Dispose();
Hud.Toast("Saved!", ToastKind.Success, seconds: 3);  // any thread
```
`Ui.Frame += ui => …` runs every overlay frame for custom drawing. For drawing shapes, text and images every frame,
in pixels or at world positions with the game's camera, use `RenderFrameEvent` ([render.md](render.md)): it draws under
Kiln's windows and turns off a handler that is too slow.

Kiln's own toast: when a server refuses this client (Kiln.Network's REJECT), Kiln.Ui shows who refused and the mods it
named (else its reason) for 20 s, over the game's login error dialog that follows, and again for 30 s when the game is
back at its main menu (`ui: refusal shown` in kiln.log). Mods don't need to handle `Network.Rejected` for that.

## Key bindings
The game's chat field keeps Up/Down (no modifiers) while it has typing focus when the player has chat history on
([chat-history.md](chat-history.md)): key subscribers do not get those presses then.
```csharp
var b = KeyBindings.Register("mymod.toggle", "F8", () => w.Toggle(), "Toggle my window");
b.OnRelease = …;  b.IsDown;  b.TakePresses();  b.Consume = false;  b.WhileCapturing = true;
```
- Keys are written like `F8`, `Ctrl+K`, `Shift+Alt+F2`, `Insert`, `Numpad5`, `Backquote`, `None`.
- **Every key has a name** (0.2.0-dev.28): what the Keys page shows and keybinds.json stores always reads back at the next
  start. Besides the names above: `PrintScreen`, `LeftWin`, `ContextMenu`, `LeftShift` … `RightAlt`, the media and browser
  keys (`VolumeUp`, `MediaPlayPause`, …), the OEM keys (`Oem8`, `IntlBackslash`, `IntlRo`, `NumpadComma`, `OemClear`, …),
  and a modifier key by itself as the last word (`Ctrl`, `Shift+Alt`). A key with no name is `Key` and its decimal
  virtual-key code: `Key223`. `Key3` to `Key255` parse (`Key1` and `Key2` are the left and right buttons: refused). The
  whole table is `native/services/ui/key_names.h`; Kiln.Ui uses the same one. In 0.2.0-dev.27 a key with no name was saved
  as `Key223` and lost at the next start (GitHub issue 33); such a line now loads and fires on that key again.
- **Punctuation names are physical keys** (0.2.0-dev.28): `Backquote`, `Minus`, `Equals`, `LeftBracket`, `RightBracket`,
  `Backslash`, `Semicolon`, `Quote`, `Comma`, `Period`, `Slash`, `IntlBackslash` (the ISO key left of Z), `IntlRo` and
  `NumpadComma` name the key at that place of a US keyboard, on every layout. `Backquote` is the key left of 1 also on a
  UK layout (which gives it `VK_OEM_8`), a German one (`VK_OEM_5`) or a French one (`VK_OEM_7`); it no longer fires on
  the UK `'` key, which sends the US backquote's code. Kiln tells the place by the key's scan code (raw input and the
  window's key messages carry it, under Wine and Proton too); a key sent without one (an injected key) counts as the US
  virtual key. `KeyChord.VirtualKey` holds the US layout's code of the place (`0xC0` for `Backquote`).
  - Letters, digits and every other key stay virtual keys: `K` is the key that types K, wherever the layout puts it.
  - A punctuation key a layout puts somewhere else (AZERTY's `,` where US has M; Dvorak's punctuation on letter places;
    the Japanese Yen key) has no place of its own: it counts as its virtual key's US place too. The Keys page saves
    what it can find again: for such a key that is the US place of its code.
  - The Keys page saves a punctuation key by its place: pressing the key left of 1 on any layout saves `Backquote`.
  - The Kiln menu's key follows the same rule. `UiWheelOptions.HoldKey` and kiln.ui's `take_pressed_key` (C ABI) stay
    plain virtual-key codes.
- **The mouse** (0.2.0-dev.27): `Mouse4`, `Mouse5` (the side buttons), `MouseMiddle` (also `Mouse3`), `WheelUp`,
  `WheelDown`, `WheelLeft`, `WheelRight`, with Ctrl, Shift and Alt as for keys: `Ctrl+WheelUp`, `Shift+Mouse4`.
  - The wheel gives one press per notch (a high-resolution wheel's smaller steps add up to a notch; turning the other
    way starts a new one). A notch has no release: `OnRelease` never runs for it and `IsDown` stays false. The buttons
    are pressed, held and released like keys.
  - The left and right buttons cannot be bound: Kiln's screens need them. `KeyChord.Parse("Ctrl+LeftClick")` (and
    `Mouse1`, `Mouse2`, `RightClick`) throws a `FormatException` that says so; `TryParse` answers false.
  - A binding that takes a press keeps it from the game as for a key: `Ctrl+WheelUp` bound by a mod does not also zoom
    the game's camera, and the wheel without Ctrl still does. A press no binding takes reaches the game untouched. A
    high-resolution wheel's smaller steps are asked as they come (not presses) and kept like the notch they belong to.
    A raw record that also carries motion or another button's input reaches the game with only the taken press taken
    out (kiln.ui edits what the game's own `GetRawInputData` import reads); motion and the other buttons stay.
  - While a Kiln screen or the Kiln menu has the mouse, no mouse binding fires, `WhileCapturing` or not: the wheel
    scrolls the screen and the buttons are the screen's (a button's release still ends a hold that began in play). The
    Keys page still takes them as a binding's new key.
  - The wheel without Ctrl or Alt fires only during play or while a Kiln screen is open, like a key that types (the
    game's own screens scroll with it); `OnGameScreens = true` fires it everywhere. The buttons always fire.
  - `KeyChord.VirtualKey`: the buttons keep their Windows virtual keys (`0x04` middle, `0x05` Mouse4, `0x06` Mouse5); the
    wheel has none and uses `0x100` up, `0x101` down, `0x102` left, `0x103` right (above every key code,
    `KILN_UI_VK_WHEEL_*` in `kiln_ui.h`). keybinds.json stores the names, never the codes.
  - A Kiln older than 0.2.0-dev.27 does not know these names: there `KeyBindings.Register("x", "Ctrl+WheelUp", …)`
    throws. A mod that should still load there can test `KeyChord.TryParse("Ctrl+WheelUp", out _)` first. A keybinds.json
    line an older Kiln cannot read leaves that binding on its default and stays in the file.
  - The Kiln menu's own key can be a mouse button but not the wheel (it would close the menu as you scroll it).
  - The mouse needs the kiln-ui.dll of the same release (kiln.ui v7). Next to an older kiln-ui.dll, which only a hand-mixed
    install has, the names still parse and `Register` works, but mouse bindings never fire and the Keys page takes keys
    only; kiln.log says so once ("this kiln-ui.dll is older than Kiln.Ui").
- **A game controller** (0.2.0-dev.30.5, mod API 0.2.14, GitHub issue 119, thanks Fuzen777): `Pad:A`, `Pad:B`, `Pad:X`,
  `Pad:Y`, `Pad:LB`, `Pad:RB`, `Pad:LT`, `Pad:RT` (the triggers), `Pad:LS`, `Pad:RS` (a stick pressed: L3, R3),
  `Pad:Start`, `Pad:Back`, `Pad:Up`, `Pad:Down`, `Pad:Left`, `Pad:Right` (the d-pad), and combinations with the
  shoulder buttons and triggers held: `Pad:LB+A`, `Pad:LT+RS`, `Pad:LB+RB+Y`. `KeyBindings.Register("fav.mark", "Pad:LS", …)`
  works like a key; the player can put any binding on the pad on the Keys page.
  - Kiln reads the pad where the game does (XInput: an Xbox pad, or any pad Steam Input presents as one), every
    controller, at each poll of the game. A binding fires on the thread the game polls the pad from, during play and on
    the game's own screens (the inventory too), never while a Kiln screen has the input (the pad navigates the screen).
  - A matching binding keeps the button from the game (`Consume`) until it is let go: the game reads it as not pressed (a
    trigger as not pulled). The buttons held for a combination (LB, RB, LT, RT) reach the game: `Pad:LB+A` keeps A, not LB.
  - `KeyChord.IsPad`; pad chords use `KeyMods.PadLB`, `PadRB`, `PadLT`, `PadRT` and never mix with Ctrl, Shift or Alt
    (`"Ctrl+Pad:A"` is refused with the reason). Codes: 0x200 and up (`KILN_UI_VK_PAD_*` in `kiln_ui.h`).
  - The Kiln menu's own key is never a pad button (it is opened from the keyboard or a mouse button).
  - On the Keys page: click the key, then press a pad button, or hold LB, RB, LT or RT and press another for a
    combination; let go of LB, RB, LT or RT alone to bind that button.
  - A Kiln older than 0.2.0-dev.30.5 does not know these names (`KeyChord.TryParse` says false; `Register` throws). Next to
    an older kiln-ui.dll (a hand-mixed install) pad bindings register but never fire.
- The player's choice is kept in `kiln/config/keybinds.json` (`{"bindings": {"mymod.toggle": "F9"}}`). The Kiln menu's
  **Keys** tab remaps keys by press: click the key, press the new one (a key, the middle or a side mouse button, or a
  notch of the wheel, with the modifiers held); a second click cancels. Left and right click keep working the page.
  Escape cancels too and closes the menu (kiln.ui closes it before Kiln.Ui sees the key). A pending "press a key" ends
  when the menu closes or another section shows: a key pressed in play is never taken for it, and reopening the menu
  shows the key unchanged.
- Handlers run on the game's main thread (the window thread) when the key goes down, but not while a Kiln text field
  has focus, and not while a Kiln screen has the input unless `WhileCapturing` is set.
- A matching binding keeps the key from the game (`Consume`, default true), including its raw-input press (for the
  mouse: the raw record and the window message, each press counted once although the game's window gets both).
- **A key that types** (a letter, digit, punctuation or Space, without Ctrl or Alt) fires only during play or while a Kiln screen is open. The
  game's own text fields (chat, a rename, the main menu's boxes) don't tell Kiln they have the keyboard, so a tree bound to K used to open inside
  chat and take the rest of the line. `b.OnGameScreens = true` fires it on every game screen too. Function keys and Ctrl/Alt chords always
  fire. Without the game UI hooks (an unknown build) the screen isn't known and they fire as before.
- `kiln.creative` (default **F7**) opens Kiln's creative menu (items with level, rarity, upgrades and gem slot, a
  backpack grid to place them in a chosen slot, time of day, the day and night cycle, map reveal, character flight; also
  **Creative** in the pause menu for admins and players in creative mode): docs/kiln/services/rules.md "Creative menu".
  `kiln.creative.flight` (no default key) toggles character flight in creative mode. Neither is registered while
  Creative Mode (Nexus 146) runs in the game: its own menu applies.
- `kiln.menu` (default **Insert**) opens the Kiln menu. Its toggle is native, so it works without the managed host.
  Escape closes the menu.

### Key and mouse presses as they come (`KeyInput`, since 0.2.0-dev.29.19)
For a mod that reads presses itself rather than giving an action a key: its own "press a key" field, a key it handles by
state, a shortcut layer of its own. An action the player should be able to remap on the Keys page stays a
`KeyBindings.Register` binding.
```csharp
var listener = KeyInput.Listen(p =>
{
    if (!p.Down || p.Repeat || p.Captured || p.TextInput) return false;  // play only, presses only
    if (!_capturing) return false;
    _capturing = false;
    Save(p.Chord.ToString());                 // "Ctrl+K", "Backquote", "Mouse4", "WheelUp": the names keybinds.json uses
    return true;                              // keep this press from the game
});
if (!listener.Ok) Context.Log.Info(listener.Reason!);  // no Kiln overlay here (a dedicated server)
listener.Dispose();                           // or let the mod's unload end it
```
- `Listen` is kiln.ui's key subscription (`subscribe_key_ex` in `kiln_ui.h`). With `KeyListenFlags.Mouse` (the default)
  it gets the middle and side mouse buttons and the wheel's notches too, as key bindings do; `KeyListenFlags.None` gets
  keyboard keys only. Next to an older kiln-ui.dll (a hand-mixed install) it gets keys only and `listener.Mouse` is false.
  `KeyListenFlags.Pad` (0.2.0-dev.30.5) adds a game controller's buttons (`KeyPress.IsPad`, the pad's modifiers in
  `KeyPress.Mods`), on the thread the game polls the pad from; true for a press keeps the button from the game until it is
  let go. `listener.Pad` says whether it gets them.
- It runs on the game's window thread while the message is handled, before the game gets the press: keep it short.
  Returning true keeps the press from the game. Every listener and Kiln's own key bindings get every press whatever
  another returned; kiln.ui keeps a press from the game when any of them asked.
- Every press comes, also while the Kiln menu, a Kiln screen or a Kiln text field has the input: `KeyPress.Captured` and
  `KeyPress.TextInput` say so. `Down` is false for a release; a wheel notch is a press with no release. `Mods` holds
  Ctrl, Shift and Alt.
- A key press that a module of the game's process sends (`SendInput`, `keybd_event`) while a Kiln text field has the
  keyboard reaches the listeners and the game as before, but never that field, as a key or as a character (since
  0.2.0-dev.31.16: a mod that taps B when its own key goes down no longer types a "b"; kiln.log names the sender, up to 3
  lines). Characters sent as text (`KEYEVENTF_UNICODE`) still type, and input from outside the game's process (an
  on-screen keyboard, a remapper) is not affected.
- `KeyPress.Code` (and `KeyInput.PressCode(vk, scan)`) is the code the Keys page stores: a punctuation key by its place
  on a US keyboard, found by its scan code on any layout, every other key by its virtual key. `KeyPress.Chord` is the
  press as the Keys page saves it, and its `ToString()` parses back with `KeyChord.TryParse` and `KeyBindings.Rebind`.
- The listener belongs to the calling mod (kiln.ui is told whose it is) and ends when the mod unloads. A handler that
  throws is logged under its mod and keeps nothing. `Listen` returns a listener that is not `Ok`, with a `Reason`, where
  there is no Kiln overlay; it never throws for that.
- A press kept this way is kept alone: its release still reaches the game and the other listeners. To own the release too
  (a gesture, an overlay that closes on a button), add `KeyListenFlags.Own` (below).

### Owning a press until its release (`KeyListenFlags.Own`, since 0.2.0-dev.31.13, mod API 0.2.22)
A listener that keeps a press keeps only that press: before 0.2.0-dev.31.13 its release still reached the game and every
other listener, and a mouse button the game never saw go down still showed as held in the game's mouse messages. With
`KeyListenFlags.Own` the press is the listener's own until it is let go: the press, its repeats and its release go to that
listener alone, and none of them reaches the game. This is what a gesture needs (press, drag, release), and an overlay that
closes on a button: the game never sees that button's release either. Thanks to XHL, whose Blueprint and overlays asked
for it.

**Blueprint's right-button gesture** (press, drag, release; the camera aims while the button is held):
```csharp
// Owns the right button while a blueprint is in the hammer: the game neither opens its build menu on the press nor
// sees the release, and is not told the button is held while the player drags.
var gesture = KeyInput.Listen(p =>
{
    if (!p.IsPointer || p.VirtualKey != 0x02) return false;  // the right button only
    if (p.Ended != KeyOwnEnd.None)                            // focus loss, a world change: drop the half-made selection
    {
        Selection.Cancel();
        return false;
    }
    if (p.Owned)                                              // the release of the press below (or a repeat)
    {
        if (!p.Down) Selection.Commit();                      // the answer to a release counts for nothing
        return false;
    }
    if (!Blueprint.Active || p.Captured) return false;        // not ours: the game gets the press and its release
    Selection.Begin(p.Position!.Value);                       // the drag: the mod follows the aim each frame until the release
    return true;                                              // own this press until its release
}, KeyListenFlags.Own | KeyListenFlags.Pointer);
if (!gesture.Ok) Context.Log.Warn(gesture.Reason!);           // a Kiln older than 0.2.0-dev.31.13, or a dedicated server
```

**An overlay that closes on a controller button** (B, or Escape on the keyboard) without the game seeing the release:
```csharp
KeyListener? close = null;
close = KeyInput.Listen(p =>
{
    if (!p.Down || p.Owned || !Overlay.IsOpen) return false;            // presses only, while the overlay is open
    if (!(p.IsPad && p.VirtualKey == 0x20D) && p.VirtualKey != 0x1B) return false;  // Pad:B or Escape
    Overlay.Close();
    close!.Disown(p.VirtualKey, p.Controller);  // closed: nobody wants the release; it still never reaches the game
    return true;
}, KeyListenFlags.Own | KeyListenFlags.Pad);
```
A button held before the overlay opened is not the overlay's (it went down before), so its release reaches the game as
usual.

- **Who is asked.** Owning listeners are asked about a new press before every listener without `Own` and every key
  binding, after Kiln's own menu key and Escape (which close Kiln's menu and screens first). They are asked in the order
  they were made; the first that returns true owns it, and the ones after it never see it. One that returned false saw the
  press and gets nothing more of it.
- **What the owner gets.** The repeats of a held key (`Down`, `Repeat`, `Owned`) and the release (`Down` false, `Owned`);
  what it returns for them counts for nothing. `KeyPress.Position` is the cursor (client pixels) for the left and right
  buttons; `KeyPress.Controller` the controller (0 to 3) for a pad's buttons. `listener.Owns(vk, controller)` says
  whether it owns a press now.
- **What the game gets.** Nothing of the press: not the key or button message, not its raw input record (a record that
  also carries motion or another button reaches the game without the owned flag, through Kiln's hook on the game's
  `GetRawInputData` import), not the button's held bit in its mouse messages (`MK_RBUTTON` and the like) while the owner
  holds it, not a controller button (it reads as up, a trigger as not pulled) until the release.
- **When it ends early.** `KeyPress.Ended` says why on a release that is no real release: `Focus` (the game window lost
  the focus: the real release goes to another window; if it comes back it reaches the game, which never saw the press, so
  nothing is held; a controller's press does not end with the focus, as XInput does not follow it: its release still
  comes and is the owner's), `World` (the client world started or stopped), `Lost` (the input went down again before its
  release came). `listener.Disown()` (one press, or all), `Dispose()` and the mod's unload end it without a call. After a world
  change, a disown or an unload the press is nobody's: its release reaches nobody, and still not the game.
- **Stuck-press guard.** Where the game saw the press go down after all, the release must reach it, or the game would hold
  the button: Alt+F4 (always the game's), or a raw mouse record with other input on a build where Kiln could not hook the
  game's `GetRawInputData`. The owner still gets the repeats and the release, with `KeyPress.Leaked` true.
- **What can be owned.** Keyboard keys always; with `KeyListenFlags.Mouse` the middle and side buttons; with
  `KeyListenFlags.Pointer` the left and right buttons (over the game's own view: not while a Kiln screen or the Kiln menu
  has the input, nor over a Kiln panel; the release comes wherever the cursor is then); with `KeyListenFlags.Pad` a
  controller's buttons and triggers (XInput's: an Xbox pad, or any pad Steam Input presents as one). The wheel cannot be
  owned (a notch has no release): an owning listener gets no notches, so listen without `Own` for them.
- **Threads.** Keys and mouse buttons come on the game's window thread, a controller's buttons on the thread the game
  polls it from, an end for a world change on the game's UI thread. Keep the handler short.
- **Where.** It needs kiln.ui.input (Kiln 0.2.0-dev.31.13 or later, on the game client): elsewhere the listener is not
  `Ok` and its `Reason` says so. Native plugins use the same service in C: `kiln_ui_input.h` (`subscribe_owned`,
  `disown`, `owns`), with the rules in `native/services/ui/input_own.h`.
- **Tests.** `KilnHarness.Input` (Kiln.TestKit) plays presses, releases, a focus loss, a world change and a leaked press
  against the real `KeyInput` and records what reached the game (`Game`) and the other handlers (`Others`).

### The controllers' sticks and triggers (`PadInput`, since 0.2.0-dev.31.13, mod API 0.2.22)
A read-only view of each XInput controller as the game's last poll read it (before Kiln hid a kept or owned button from
the game), for a mod that follows the sticks or the triggers rather than presses:
```csharp
var pad = PadInput.Read();                       // controller 0; Read(1..3) for the others
if (pad.Connected && pad.RightTrigger > 0.5f && pad.LeftStick.Y > 0) Climb.Up(pad.LeftStick);
if (pad.IsDown(PadButtons.LB | PadButtons.A)) Climb.Jump();
```
Sticks are -1 to 1 with XInput's radial dead zone taken out (0 inside it), triggers 0 to 1 (above 0 from a pull of 30 of
255, when `PadButtons.LT` / `RT` count as held, as "Pad:LT" bindings do). `Owned` and `Hidden` say which buttons an owning
listener holds and which the game reads as up. `RawSticks` and `RawTriggers` are XInput's values. It never changes what the
game reads. `PadInput.Available` is false where Kiln does not read controllers (no XInput loaded yet, a dedicated server,
an older Kiln). A pad the game reads through DirectInput (some controllers without Steam Input) is not seen.

### XHL-Core (Kiln): from reflection to public members (0.2.0-dev.29.19)
XHL-Core (Kiln) 0.1.0 reads a few Kiln internals by name. They stay as they are (Kiln.Ui.Tests' XhlKilnCoreReflectionTests
pins each name and shape), so the published 0.1.0 keeps working; on 0.2.0-dev.29.19 and later the public members below do
the same jobs and are covered by the API promise (`PublicAPI.Shipped.txt`, never removed before API 1.0.0):

| Read today by reflection | Public member |
|---|---|
| `Kiln.KilnText.Language` (static, non-public) | `KilnLanguage.Current` |
| `Kiln.KilnText.Gated` | `KilnLanguage.OverlayInEnglish` |
| `Kiln.KilnText.Changed` (event `Action`) | `KilnLanguage.Changed` (event `Action`; guarded per handler, dropped at the mod's unload) |
| `Kiln.UiNative.T` with `Table7.SubscribeKeyEx` (flag `KILN_UI_KEYS_MOUSE`), the owner from `ModContext.Owner` | `KeyInput.Listen(onKey, KeyListenFlags.Mouse)`; the owner is the calling mod, filled in by Kiln |
| `Kiln.UiNative.Table.Unsubscribe` | `KeyListener.Dispose()` (also done at the mod's unload) |
| `Kiln.KeyNames.PressCode(vk, scan)` | `KeyInput.PressCode(virtualKey, scanCode)`, or `KeyPress.Code` / `KeyPress.Chord` inside a listener |
| `KeyBinding.Category`, `KeyBinding.Description` | already public (read them directly) |

`ModContext.Owner` stays internal: it is the mod's native owner handle, and a mod holding it could register, and revoke,
native subscriptions in any service as that mod. Its one use here, the key subscription, is now `KeyInput.Listen`, which
passes the owner itself. A mod that needs a native service Kiln has no managed member for should ask for the member (below)
rather than for the handle.

**Where new APIs come from.** Hooks and call sites a mod author has found in the game (as XHL has for his native mods) are
welcome as proposals for Kiln's general API, so that every mod can use them: a GitHub issue on
[frostdev-ops/kiln](https://github.com/frostdev-ops/kiln/issues) naming the game function or data, how to find it (a
pattern or the code around it), and what a mod would do with it. Kiln adds such a site as a sigdb feature with its own
capability row ([services/caps.md](services/caps.md)), then a typed event or service member in the Kiln.* assembly it
belongs to (a UI site in Kiln.Ui, a gameplay site in Kiln.Gameplay, and so on). Until then an advanced mod reaches a site
itself through `Kiln.Hooks` ([hooks.md](hooks.md)).

## The Kiln menu (the mod manager)
A page in the player's look (the game's colours and its UI font unless the player chose another look under **Kiln
settings > Look**, [Looks and themes](#looks-and-themes-020-dev2916)), opened by the menu key (Insert), the main menu's and the
pause menu's **Mods** entry; Escape (gamepad B) or the **Back** hint goes back. A title, a tab row of sections, a footer
of key hints. Tab / Shift+Tab or LB / RB switch sections; arrows or the d-pad move; Enter or A chooses; Ctrl+F jumps to the search box.
- **Mods**: three columns.
  - Left: **All mods**, **Needs attention** (only when a mod failed or its folder is missing), then one row per engine
    that has mods (Kiln, native DLL mods, Kiln plugins, chain DLLs, EMF, EML, XHL, ShroudForge, SET, Greeneye, Flame,
    Shroudtopia, Enshwick, shroudlink, Ember) with its count and a mark (green fine, grey idle, red a problem), and
    **Kiln settings**: the **Look** of Kiln's UI (always on a game client; [Looks and themes](#looks-and-themes-020-dev2916)),
    then Kiln's own configs.
  - Middle: the mods of that row (name, version, a state chip: Loaded, Waiting, Off, Failed, Not loaded, Missing; the
    engine when mixed), a search box that filters as you type (every word must match the name, id or engine), the engine's
    one-line state when it matters ("ShroudForge: runtime hosted by Kiln.", "EMF: stepped aside for the real EMF",
    "Ember: mods are found but cannot run yet."), and an empty state that says what to do.
  - Right: the selected mod: name, id, version, state, engine and side chips, the full reason or error and what to do
    about it, the engine, the folder; for Kiln mods also the load order, what it needs, its status lines
    (`StatusLines`) and its settings inline: the page generated from its config schema (`ConfigScreens.cs`; server
    configs are read-only while connected) and the pages it registered with `Ui.AddConfigPage(modId, ui => …)`.
    On the generated page a number with a `[Range]` is a slider with a number field beside it, where a value is typed
    (`[Range(min, max, step)]`: the slider snaps to min + n * step, the typed value is kept as typed; with
    `SoftMin` / `SoftMax = true` the field takes a value past that end of the slider, which then sits at its end);
    edits are marked and saved with Apply, and the table shows the applied values at once, also while the game is
    paused and the save waits for the next tick.
    ShroudForge mods have an enabled switch and settings from `extended.mod.json`, grouped and conditionally enabled,
    with live/restart hints, typed options, text lengths and declared action buttons. An action is sent only while
    ShroudForge's runtime runs the mod in this game (its `state.json` lists it active), and only one request of Kiln's
    per mod waits in ShroudForge's actions folder at a time, one left by an earlier game start included (ShroudForge keeps
    a request it cannot hand over for ever and may run it at a later start; the page then says one is still waiting). Slider writes settle for 350 ms;
    text saves when the field loses focus (Tab and LB / RB do not switch sections while a field has the keyboard, and
    text typed into a field that goes away before it is left is dropped, never shown as the value); a field holds the whole value, and one too long to edit (64 KiB on one line,
    1 MiB in a text area) is shown, not edited. Choices use combos (including radio/segmented), colors use text, and SF
    keybinds use editable canonical SDL strings rather than press-to-capture. Invalid edits leave the file unchanged. A mod whose settings an earlier Kiln wrote without
    their fraction (ShroudForge refuses such a mod) shows Failed, "needs repair", with a line on its page: changing any
    setting or its switch once repairs it.
    Reads/writes are off the render thread, bounded to 2 MiB per package file, and refuse linked package files.
    Comments and formatting survive value edits. A profile-controlled switch is marked because profile activation
    reapplies it at launch. See [shroudforge.md](shroudforge.md) for SF shortcut/helper ownership and remaining client checks.
  The selection follows the gamepad / keyboard focus and is kept while the game runs.
- **Network**: Kiln.Network's status, the server's Kiln version, warnings and mods, and clients and channels when hosting.
- **Keys**: every binding, with remap and reset.
- **Overlay**: resolution, scale, frame cost, error, a test toast.
- **Graphics** (0.2.0-dev.29.18): Kiln's own frame cap, DLSS render preset and render scale, and a readout of frame rates
  and the upscaler in use, with what they do now and what they step aside for: [graphics.md](graphics.md).

Where the list comes from (`ModCatalog.cs`, internal; usable on a dedicated server, which has no menu):
`Mods.All` for Kiln mods; `<game>/kiln/state/` for the rest: `boot.json` (chain DLLs, Kiln plugins, the profile in use),
`native.json`, and each engine plugin's file (`emf.json`, `eml.json`, `set.json`, `greeneye.json`, `flame.json`,
`shroudtopia.json`, `shroudlink.json`, `ember.json`; contracts.md 5.9). A profile entry of an engine that reads its mods
only from its own folder in the game (ShroudForge's configured mods folder, SET's `shrouded_engine_tweaks/mods`;
kiln-manage `engine_mods::placed_folder`) is shown as the copy that engine runs, `<folder>/<id>`, when Kiln's marker
there names the entry's kind and id or the player's own folder there holds the same package (`EngineRunFolder`,
0.2.0-dev.29.18): its details' FOLDER line names that folder, and the ShroudForge panel reads and writes the settings
there, which profile updates keep. In Copy path mode the profile names a second copy under `kiln/profiles/` that the
engine never reads; before, the page showed and edited that one. ShroudForge's packages come from the profile and
its configured mods folder; its host state comes from `shroudforge.json`, with actual per-mod activation/errors and
reported effect state/detail from SF's own `state.json` runtime section (matching PID, running, heartbeat from this
process start and no older than 30 s). A mod reports `waiting`, `no-target`, `no-change`, `write-confirmed` or `write-failed`; ShroudForge's
runtime also sets `loading`, `loaded`, `error` and `restart-required`. As in the launcher, `write-failed` and `error`
show Failed (and count under Needs attention), `write-confirmed`, `loaded` and `no-change` show Loaded, and every other
state (pending, or a word Kiln does not know) shows Waiting. A running adapter alone is not proof a
Lua mod is active, and reported readback is not gameplay proof. A mod with no Lua part (no `runtime` capability) is
never in that runtime section; its proof is the `assets` section of the same file, ShroudForge's record of its startup
game-data pass. The record has no pid. It describes this start in two cases: its `completedAt` is inside this
process's lifetime, or kiln-shroudforge's status of this process (`kiln/state/shroudforge.json`) has
`startupPrepared: true`. The second is the usual start: ShroudForge finds the pass of an earlier start still current
for this start's mods and leaves the record as it is, with that start's time; the adapter sets the flag when
ShroudForge's startup preparation returned success, which it also does after a pass that failed and restored the saved
baseline (it writes a fresh `failed` record first). The flag says only that the record is this start's; what happened
is the record's own `status` beside it. Then an `applied` pass on this side's target (`enshrouded` on a
client, `enshrouded_server` on a server) that lists the mod at the version in its folder shows Loaded with the word
`applied`; a `failed` pass of this process's lifetime shows Failed with its error. Any other record (an earlier start
without the adapter's flag, the other target, a pass still `preparing`, the mod missing from the list or listed at
another version) proves nothing either way, and the mod shows Waiting with the word `startup`. While ShroudForge
runs, the EML rows of packages in its mods folder are read the same way (ShroudForge runs a package there only while
its `extended.mod.json` says `enabled: true`, on the sides its `targets` name): switched off there they show Off,
word `off-in-shroudforge`, with where to switch it on (Play in the launcher for Kiln's copy; for the player's own
folder the words promised on Nexus, 2026-11-14: "You put this mod into ShroudForge's folder yourself, and ShroudForge
has it switched off.", then Mods > ShroudForge or removing the folder and pressing Play), and the EML engine line
counts them as a problem; on, a mod
with a startup part shows `applied` when the record lists it, else Waiting `unconfirmed`. Kiln's EML copies are not
repeated in the ShroudForge group; a player's own legacy EML folder there (no `extended.mod.json`) is listed in it,
switched off, with the switch that writes ShroudForge's file, and the same words ("Switch it on below, or remove the
folder and add the mod with Add mods in the Kiln launcher."; `ModCatalog.HandEmlOff`). Enshwick's mods
come from the profile and mods folder, with loader state from `enshwick.json`; a folder there that another kind owns
(a profile entry of another kind, a `native-plugin.ini`, a `mod.json`, or Kiln's placement marker of another kind) is
not listed as Enshwick's. XHL companions (the profile's `mods.xhl`, and folders with XHL-Core's `native-plugin.ini` in
`<game>/mods/`, but not a ShroudForge package there while ShroudForge reads that folder: Kiln's marker says
shroudforge, or its `extended.mod.json` is ShroudForge's own and not an EML package's switch; ShroudForge's own DLL
loader loads that file's DLL, so the package is one ShroudForge row, 0.2.0-dev.29.18) are their own group: "placed for XHL-Core; Kiln does not load it", with the XHL-Core prerequisite, never
Loaded, since Kiln never runs them and nothing reports whether XHL-Core did. A switched-off entry whose folder is still
in `<game>/mods/` (XHL, EML mods a DLL reads there, Kiln's copy for Enshwick) says that a loader reading that folder may
still run it. A mod the
profile lists that no report names shows as off (switched off in the profile), missing (its folder is gone) or not
loaded, with the reason. A file from an earlier start (another `pid`, or written before this process started) does not
count. The state words are read as the launcher reads them (`EngineStatus.tsx`), so both say the same.

Cost: no catalog refresh while the menu is closed. While it is open the list is read on a worker at most every 2 seconds;
the selected SF package refreshes at most once per second, and files whose bytes did not change make no new package.
What the page needs of a package (its first 300 settings by group, which can be changed now, its texts cut to 2000
characters) is made on the worker that read it, and again after an edit or an error (at once for a package of at most
300 settings whose value text totals at most 4096 characters, on a worker for a larger view), never per frame. A package
with more settings shows the first 300 and a line saying how many more it has. Choices, groups and actions have a
separate combined limit of 1000; a package over
that limit is not edited here, with a message pointing to its unchanged `extended.mod.json` (choices are never cut
and saved back as a partial list). A package with a value no reader can use (a string with an unpaired surrogate escape, a number
no double holds) is found on the read worker and shown as "cannot be shown", never thrown on the render thread. The render thread draws snapshots (no file access or waiting);
rows are made when the list, filter or search change. SF's fallback shortcut worker watches host state at low frequency,
then waits on its named toggle event with a two-second lifecycle check; it exits when SF stops or owns its own helper.

### Two hosts
The page draws into a rectangle at a scale (`ModManagerPage.Draw(ui, rect, scale)`). Two hosts give it that rectangle;
they share the title and the footer (`ModManagerChrome`, one look each).
- **In the game's main menu** (`GamePageHost`, `GamePage.cs`). Used when the main menu's **Mods** entry is clicked, or
  Insert is pressed while the main menu shows its entries, on a build with feature `ui.menu_page`. The game moves to a
  page of its own (its Credits slot, which Kiln leaves empty): the camera glides to the close-up shot, the main menu
  goes, the game's vignette stays. The Kiln page draws from the first frame the game shows it, inside the game's page
  padding (80 / 50 UI units), at the game's UI scale, with no background of its own: the title is the game's header
  (bold, upper case, white), the footer its hint bar. Leaving asks kiln.ui (`menu_page_leave`); the game goes back
  itself, with its back sound and camera. If the game leaves the page by itself (an invite), the page just goes.
  While a game error popup shows, the page steps aside and the game gets the input.
- **Stand-alone** (`ModManagerHost`): the whole display over a dimmed game, a large title. Used for the pause menu's
  **Mods** entry, Insert in a world, and every case where the game page is not available. Its scale is the UI scale
  times the display's growth (1920x1080 = 1), but never so large that the content between the margins is narrower
  than 886 units, the Mods section's three columns at their smallest (`ModManagerHost.PageScale`,
  `ModManagerPage.Columns`). At 300 % Windows scaling on a 3840x2160 display that is 3.86, not 6.

Fallback: when a symbol of `ui.menu_page` does not resolve (another game build), the page is not where the main menu
is (another page shows), the game faults in its page switch, or no host draws the page for about a second, the **Mods**
entry opens the stand-alone page, as before. An older Kiln.Ui (it never calls `menu_page_host(1)`) also gets the
stand-alone page. On a kiln.ui without the layout calls below the old small Kiln window is drawn. If no managed code
subscribed to the overlay, the native side draws a minimal Kiln window saying the managed host is not running.
How the game side works, and what still has to be checked on a real client: [docs/re/game-ui.md](../re/game-ui.md)
"Main-menu pages".

`tools/ui-preview/` renders the page to PNGs on Linux without the game (its README).

## C (native plugins)
Key callbacks: `subscribe_key` gets keyboard keys only, as it always did, so a plugin built against an older `kiln_ui.h`
gets exactly what it got before (no mouse press, no new code, and it can never take a mouse press). Version 7
(0.2.0-dev.27; check `header.size >= offsetof(KilnUiApi, subscribe_key_ex) + sizeof(void*)`) appends
`subscribe_key_ex(owner, fn, user, flags, &handle)`: flags 0 is `subscribe_key`; `KILN_UI_KEYS_MOUSE` adds the middle
and side mouse buttons (`vk` `VK_MBUTTON`, `VK_XBUTTON1`, `VK_XBUTTON2`, a press and a release, `scan` 0) and one press
per wheel notch (`vk` `KILN_UI_VK_WHEEL_UP` / `DOWN` / `LEFT` / `RIGHT`, 0x100 to 0x103; never a release), with the
modifiers held then, once each although the window gets each as a raw record and as a message
(`native/services/ui/mouse_press.h` pairs them: the same input and direction at the closest message time within 50 ms,
wheel deltas reconciled, so a WM_MOUSEWHEEL that Wine merged from two turns covers both records); any other bit is
`KILN_ERR_INVALID_ARGUMENT`. A high-resolution wheel's delta that completes no notch comes as `repeat` 1, not a press:
return true to keep it from the game as you would the notch. Returning true keeps the press from the game: its message,
and its raw record (whole when it holds only the press; otherwise the game reads it without the taken flags, through
kiln.ui's hook on the game's `GetRawInputData` import; without that hook such a record reaches the game whole). Only a
subscriber that got a press can take it; releases always pass.
While Kiln captures input they come with `captured` 1. Left and right clicks never come. With no `KILN_UI_KEYS_MOUSE`
subscriber the mouse costs nothing and reaches the game untouched. Kiln.Ui subscribes this way; kiln-emf and kiln-set
stay on `subscribe_key`. `set_menu_key` takes the buttons, not the wheel; `take_pressed_key` stays keyboard only.

Owned presses (0.2.0-dev.31.13, mod API 0.2.22): the service `kiln.ui.input` v1 (`kiln_ui_input.h`, a table of its own):
`subscribe_owned(owner, sources, fn, user, &handle)` with `KILN_UI_OWN_KEYS` / `MOUSE` / `POINTER` / `PAD`; `fn` gets
`KilnUiOwnedInput` records: `KILN_UI_OWNED_PRESS` (answer `KILN_UI_OWN_TAKE` to own it), then `REPEAT`, `RELEASE` or `END`
(`end`: `FOCUS`, `WORLD`, `LOST`) of the presses it took; `KILN_UI_OWNED_LEAKED` marks a repeat or release the game gets
too (the stuck-press guard). `disown(handle, vk, controller)` (vk 0: all), `owns(handle, vk, controller)`, `unsubscribe`.
`pad_state(controller, &KilnUiPadState)` is the controllers' view `PadInput` reads. The rules are
`native/services/ui/input_own.h`; `KeyListenFlags.Own` above describes them.

The pointer and drag and drop (0.2.0-dev.30.3): `pointer(&KilnUiPointer)`, `drag_source(kind, payload, len, preview)` and
`drop_target(kind, out, capacity, &len)`, inside a frame callback, a size-guarded tail at byte 1056
([services/ui.md](services/ui.md#the-pointer-and-drag-and-drop-020-dev303)).

A window's state (0.2.0-dev.31.16, mod API 0.2.23, GitHub issue 149): `window_state(name, &KilnUiWindowState)`, inside a frame
callback, a size-guarded tail at byte 1088: where a window is now and whether the player drags or resizes it, what
Kiln.Ui's window docking reads ([services/ui.md](services/ui.md#a-windows-state-for-docking-020-dev3116-mod-api-0223-github-issue-149)).

`kiln_ui.h` (later additions, check `header.size`): `screen`, `subscribe_screen`, `subscribe_ui_tick`,
`menu_add` / `menu_set` (`KILN_UI_MENU_MAIN` / `PAUSE`), `push_style` / `pop_style`, `image_*`, `combo`,
`input_multiline`, `set_hover_capture`, and work at present under the overlay: `subscribe_pre_overlay`, `set_pre_overlay_demand`,
`add_swapchain_usage` (kiln.screen draws its full-screen effects there: [services/screen.md](services/screen.md)). Version 2
appends `is_item_clicked(button)` (a click of `KILN_UI_MOUSE_LEFT/RIGHT/MIDDLE` on the item drawn last, inside a frame callback) and
the wheel: `wheel_open(owner, &KilnUiWheel, fn, user, &handle)` (`fn(choice, user)` once on the render thread; -1 = cancelled),
`wheel_close(handle)` (ends it without `fn`) and `wheel_active()`. The pure rules (pages, hit test, hold key, tap) are in
`native/services/ui/wheel_math.h`, tested by `native/services/ui/tests/run.sh`. Version 3 appends the layout calls above
(check `header.size >= offsetof(KilnUiApi, set_scroll_here_y) + sizeof(void*)`): `push_font_size` / `pop_font_size`,
`begin_child_ex` (`KILN_UI_CHILD_*`), `set_next_item_width`, `cursor` (`KilnUiCursor`), `set_cursor_pos`, `item`
(`KilnUiItem`), `selectable_ex` (`KILN_UI_SELECTABLE_*`), `set_keyboard_focus_here`, `set_item_default_focus`,
`draw_rect_in` / `draw_text_in` (`KILN_UI_LAYER_*`), `text_size_ex`, `push_color` / `pop_color` (`KILN_UI_COLOR_*`),
`push_var` / `pop_var` (`KILN_UI_VAR_*`), `key_pressed` (`KILN_UI_KEY_*`, `KILN_UI_KEYQ_REPEAT|CLAIM`), `key_mods`,
`set_scroll_here_y`, and the window flags `KILN_UI_WINDOW_NO_SAVED_SETTINGS`, `NO_BRING_TO_FRONT`,
`ALWAYS_VERTICAL_SCROLLBAR`. Version 4 appends the main-menu page (check `header.size >= offsetof(KilnUiApi, pop_font) +
sizeof(void*)`): `menu_page(&KilnUiMenuPage)` (state `KILN_UI_PAGE_HIDDEN/OPENING/SHOWN`, the game's canvas, viewport,
pixels per unit, padding and the content rectangle `x0..y1` in pixels, `error_popup`), `menu_page_drawn()`,
`menu_page_leave()`, `menu_page_host(on)`, and `push_font(KILN_UI_FONT_GAME / GAME_BOLD)` / `pop_font()` (C#:
`using (ui.Font(UiFont.GameBold)) …`). Version 5 appends `subscribe_window_input(owner, fn, user, &handle)` (check
`header.version >= 5` and `header.size >= offsetof(KilnUiApi, subscribe_window_input) + sizeof(void*)`): the game
window's own input records for a native plugin that keeps a key state of its own (kiln-shroudforge feeds ShroudForge's
mod shortcuts with it, [shroudforge-compat.md](shroudforge-compat.md#56-mod-shortcuts-kilns-host-key-feed)).
`fn(const KilnUiWindowInput*, user)` runs on the window thread inside Kiln's window procedure, before Kiln handles the
message, also for what Kiln then keeps from the game (its menu or a screen captures input, a binding or the menu key
consumes a key, the wheel is open); it cannot keep anything itself. `KilnUiWindowInput` (48 bytes: `size, kind,
message, make_code, flags, vkey, button, down` as `uint32_t`, then `uint64_t wparam`, `int64_t lparam`) is one of:
`KILN_UI_INPUT_RAW_KEY` (every keyboard record of `WM_INPUT`: `RAWKEYBOARD.MakeCode`, `.Flags`, `.VKey` unchanged),
`KILN_UI_INPUT_WINDOW_KEY` (every `WM_KEYDOWN`/`UP`, `WM_SYSKEYDOWN`/`UP`: `wparam`, `lparam` unchanged),
`KILN_UI_INPUT_MOUSE_BUTTON` (button 1 left, 2 middle, 3 right, 4 and 5 the side buttons, `down` 1/0; from each raw
mouse record, presses before releases, and from each button message, a double click being a press; `message` says
which) and `KILN_UI_INPUT_FOCUS_LOST` (`WM_KILLFOCUS` or `WM_ACTIVATEAPP` 0, one record per loss: the second message
of the same loss gives none until the window regains the focus or gives a key or button record; coalesced per
subscription, so a new subscriber does not rearm an existing one). A key or click
usually arrives both raw and as a message, in the order the window received them; nothing is merged. Characters,
mouse motion and the wheel are not delivered. Remove it with `unsubscribe`, which waits for a call in flight (from
inside another kiln.ui callback it may answer `KILN_ERR_WRONG_THREAD` instead); a faulting `fn` is removed. Version 6
(0.2.0-dev.27; check `header.version >= 6` and `header.size >= offsetof(KilnUiApi, menu_activate) + sizeof(void*)`)
appends the mods' entries in the game's menus: `menu_shown(KILN_UI_MENU_SHOW_FEW / ALWAYS / NEVER / AUTO)` (which of them the
menus draw; ALWAYS until a caller sets it; Kiln's own Mods entry always; the rule is `native/services/ui/menu_rows.h`), `menu_entries(out, capacity)`
(every entry mods added with `menu_add`, as `KilnUiMenuEntry`: `size, menu, entry, text, text_len, flags, order,
drawn`; set `out[0].size` first; returns the count) and `menu_activate(entry)` (does what choosing it in its menu
does: `fn` after the next UI frame; refused for an entry its mod hid or one of the other menu) and `menu_scroll()`
(`KILN_UI_SCROLL_*`: whether the pause menu scrolls in this process, and why not). Colours,
vars and keys are Kiln's own numbers (stable across ImGui updates). The plain ImGui
slots live in `native/services/ui/draw_slots.h`, shared with the preview shim. `kiln.ui.tick` (the UI frame as a tick; Kiln.Host loads the mods with it) and
`kiln.craftmenu` (tabs, subcategories, which workshop the menu builds; implemented by kiln.content) are separate
tables in the same header.

## Cost and behaviour
- Nothing to draw (no HUD line, no open window, no toast, no frame handler, menu closed): the game's presents pass
  through untouched. No ImGui frame, no GPU work.
- Something to draw: one ImGui frame and one command buffer (one render pass over the swapchain image) per present.
  `Ui.Status.FrameMs` is the CPU cost of the last overlay frame.
- Resize, minimise and SDR↔HDR swapchain changes are handled. HDR (PQ) output is not tone-mapped yet, so the overlay
  is too bright there.
- Work under the overlay (kiln.screen) is called only while it says it has something to do; otherwise the present reads one counter.
- No wheel open: the wheel costs one atomic read per frame and per input message.
- A middle or side button or wheel message (and its raw record) looks for its twin among at most 32 waiting presses,
  only while a key subscriber takes mouse presses (Kiln.Ui does); motion and the left and right buttons cost nothing new.
- No `subscribe_window_input` subscriber: one relaxed atomic read per window message. With one, a record per key,
  button or focus message, handed over on the window thread. `is_item_clicked` costs nothing unless a
  window asks. With a wheel open it is one more ImGui window with a few shapes.
- When `vulkan-1.dll` is not loaded yet at kiln.ui's start (the usual case), kiln.ui hooks `LoadLibraryExW` and keeps
  the hook: after every load that gave a module it makes or repairs its Vulkan hooks and, once an XInput library is
  there, its gamepad hook. The game's own load runs first and under no lock of Kiln's, so loads on several threads
  never wait for each other because of Kiln, and a load that fails keeps its error (`native/services/ui/load_hook.h`;
  `native/services/ui/tests/test-load-hook-wine.sh` runs the hook in a Windows process under Wine, no game).
- `KILN_UI_DISABLE=1` turns the whole UI service off (the service is then unavailable). It also turns off the progress window of the
  early step (`kiln.progress`, [eml.md](eml.md#what-you-see-while-it-runs)).
- The game-UI hooks cost one check per UI frame and per menu-entry call (a return-address compare); entries draw only
  while their menu is open. `KILN_NO_MENU_LOAD=1` keeps the mods' load at the first world step.

## The contract (dev.27)

Kiln.Ui follows [the API contract](modding/contract.md) as far as it applies. It is a game-client service: `Ui` (with
`Ui.Screens`, `Ui.Menus`, `Ui.Crafting`), `Hud`, `Render`, `KeyBindings`, `UiDraw`, `RenderDraw` and `WorldDraw` are
`[ClientOnly]`; on a dedicated server their calls draw nothing and answer null or false, as before.

- **Capabilities.** `kiln.ui`, `kiln.render` and the client's sigdb features (`ui.screens`, `ui.main_menu`,
  `ui.menu_page`, `ui.menu_page_sound`, `ui.system_menu`, `ui.crafting`, `ui.error_popup`, `ui.language`, `ui.popups`,
  `render.camera`), lowered by Kiln.Ui when their service is not loaded. No member throws for a missing capability, so
  none carries `[RequiresCapability]`; the `Available` properties answer as before.
- **Threads.** `UiDraw` is `[RenderThread]` (it exists only inside a draw callback). `Ui.Screens.Current`,
  `Render.Camera`, `Hud.Toast`, `HudLine.Text`, `UiWindow.Open`, `MenuEntry.Text` and `StatusLines.Set` are
  `[AnyThread]`, as their docs said.
- **Events.** `Ui.Screens.Changed` (the game's UI thread, at once) and `ScreenChangedEvent` (the bus, tick thread, one
  step later) are two deliveries of one native callback, not two sources; both stay.

## Overlay startup crash guard

Before Vulkan hooks are installed, Kiln writes a versioned record to
`kiln/state/overlay-start.guard`. It records hooks installed, instance seen, device seen,
swapchain created, the overlay's first draw, and completes once the overlay's frames have been presented for a while: 60 presents with the
overlay drawn, or a 20 second window after the first in which the game presented at least 60 frames (watched window
after window while it presents fewer, as during loading). Since 0.2.0-dev.29.18; before, the first present with the
overlay drawn completed it, so a game that died a few frames later was not caught. When the overlay's drawing turns
off during a session because its own work failed ([below](#when-the-overlay-fails-during-a-session); not for a present
from a queue Kiln does not draw on, which comes before any work of its own), the record is armed again: a game that
closes before it has presented 60 more frames in a 30 second window counts as an interrupted attempt. A game that goes
on presenting does not, and neither does a normal exit (exit code 0). kiln.ui sees that in its `on_unload`, one of
Kiln's own exit steps: under Wine, and since 0.2.0-dev.29.18 on native Windows too (only kiln.json
`"unloadAtExit": "off"` turns it off). That the game's own quit on Windows ends with exit code 0 is not confirmed yet;
where it does not, a quit during the overlay's first drawn frames (about a second at 60 frames a second), or in the 30
seconds after it failed, counts as an interrupted attempt: one start without the overlay, then it tries again. After
the normal exit nothing writes the record any more, so a failure or a first draw on another thread in the moment
before the process ends cannot arm it again.
Each record is written (a temporary file, then a rename) before the stage goes on, and forced to the disk
on a thread of its own, never on the game's present call: a game that dies loses nothing it already
wrote.
A game that ends before then in any other way (a crash, a hang that was killed) counts as an interrupted attempt: the
cost is one start without the overlay.

A start whose overlay draws nothing yet (Kiln's menu and mods' windows closed) counts as fine once the game has
presented 60 frames through Kiln's hooks in a 30 second window after its swapchain, or exits normally; the overlay's
first draw arms the record again. Since 0.2.0-dev.30.2 those windows repeat until one has its 60 frames: a start whose
first frames stall (ReShade's Vulkan layer kept with `--keep-vulkan-layers` compiles its effects there, a slow first
load) counts as fine once the game presents normally. Before, the one window after the swapchain was all: such a start
stayed an attempt for its whole session, and any end of it that was not a normal exit read as a death at the overlay's
start. The overlay's first draw is the first present on which Kiln's drawing does work of its own (its Vulkan
resources, ImGui, its frame, its submit), not a present Kiln got no further on (no swapchain or device of its own seen,
a queue it does not draw on). A start that died after its swapchain was made but before that first draw (stage
"swapchain created") did none of the overlay's own work: since 0.2.0-dev.30.2 it guards the one start after it and
never counts towards the lock below; kiln.log says "before Kiln's overlay drew anything".

On the next start only overlay drawing steps aside. Keys, native game-menu rows and
`kiln.ui.tick` remain available. The drawing capability rows in
`kiln/state/capabilities.json` carry the reason. The log says:

> ui: the game closed during the last start while Kiln's overlay was starting (stage: instance seen); the overlay's drawing stays off for this start, and the next start tries it again; key bindings and the Mods menu still work

The stage varies. When detected it adds:
“NVIDIA Streamline (a frame generation mod) is installed; it wraps the same graphics calls”.
This is a likely conflict, not proof of the crash's cause.

The start after a guarded start tries the overlay again, however long the guarded start ran and however it ended. With
the drawing off Kiln installs none of its Vulkan hooks, so the guarded start arms no attempt of its own, and its end
(a quick quit from the main menu, or a crash) says nothing about the overlay. The warning above says "the next start
tries it again" unless something else keeps the drawing off then too (automatic Streamline or OptiScaler protection,
`KILN_UI_OVERLAY=0`, kiln.json `ui.overlay`); those still take precedence. Until 0.2.0-dev.29.18 a guarded start had to
run a minute for that: one that ended sooner left the next start guarded too, and so on at every start, until the
record was deleted. Such a record gets its new attempt at the first start of 0.2.0-dev.29.18. The record keeps the
first interrupted attempt: a second one at the same stage (any stage but "swapchain created") keeps the drawing off at every start (a normal exit does not
change that) until the record is deleted or Kiln is updated. Since 0.2.0-dev.29.19 the record notes the Kiln
version that wrote it, and a record of another version (an update, also going back to an older one) or one without
a version (every Kiln before 0.2.0-dev.29.19) starts fresh at the next start: its attempts, its lock and its
wrapper-change recovery are cleared, and `kiln.log` says "Kiln was updated since the record was written". A Kiln
before 0.2.0-dev.29.19 cannot read the new record; it starts without it (it tries the overlay) and logs that it could
not understand the file. To re-enable it, close the
game and delete `kiln/state/overlay-start.guard`. The record is one line, `4 <phase> <stage> <failed stage> <failures>
<wrapper retry> <Kiln version> <wrappers>` (`-` for no version; format 3, from 0.2.0-dev.29.19, has no first-draw
stage, its stage 5 is the frames presented; format 2, before 0.2.0-dev.29.19, has no version; a Kiln before
0.2.0-dev.30.2 cannot read format 4 and starts without the record);
phase 0 is complete, 1 an attempt (the overlay starting, or a start that died during it),
2 a guarded start, 3 a guarded start that exited normally, 4 locked; stage 0 is before hooks, 1 hooks installed,
2 instance seen, 3 device seen, 4 swapchain created, 5 the overlay's first draw, 6 the overlay's frames presented. Removing a detected graphics wrapper
also permits a new attempt, as does adding or replacing one. The guard compares the paths,
sizes and the first and last 64 KiB of `sl.interposer.dll`, `dxgi.dll`, `ReShade64.dll`,
`opengl32.dll`, and `powrprof.dll` directly in the game folder or its `Streamline/` folder, and OptiScaler's DLL when
it is found ([OptiScaler](#optiscaler)).
Loaded Streamline and ReShade copies also count, once per file; loaded Windows system copies
of dxgi/opengl32/powrprof do not. The game's `nvngx_dlss.dll` and leftover Streamline helpers
do not count as wrappers. It does not scan arbitrary subfolders. Older presence-only guard
records get one fresh attempt after this update. Only one wrapper-change recovery is allowed
until an overlay frame succeeds; further changes keep the failure history. Timestamp-only
changes do not grant a recovery. A same-size replacement that changes only unsampled bytes
may need the guard deleted manually. Unknown/unreadable records permit normal startup. File errors are logged once
and do not prevent startup. This guard does not make Streamline and the overlay compatible.


## When the overlay fails during a session

Since 0.2.0-dev.29.18 the first failure of the overlay's own Vulkan work turns its drawing off for the rest of the game
session, and every present after it goes on as the game made it. Kiln does not try again in that session; the next
start does. The failures are: the overlay's submit fails, its resources or ImGui cannot be made, a Vulkan call of
ImGui's backend reports the device lost or `VK_ERROR_UNKNOWN`, the overlay faults, and a present that comes from a
queue Kiln cannot draw on. kiln.log says it once:

> ui: Kiln's overlay drawing is off for the rest of this game session: its Vulkan submit failed; vkQueueSubmit of the overlay's frame returned -13 (VK_ERROR_UNKNOWN) on queue family 0. The game's frames go on as the game made them (Kiln hands each present on untouched); key bindings and the Mods menu still work, Kiln's windows and HUDs do not show. The next start tries the overlay again

From then on it is the [drawing-off](#frame-generation-mods-and-drawing-off) state for this session: nothing of
Kiln's takes the game's keyboard or mouse (an open Kiln menu closes; a Kiln text field, the Mods page or a wheel lets
go at once, so key bindings see no typing and Escape reaches the game), the Mods entry shows "Mods: the overlay's
drawing is off: <reason>", `Ui.Status.Error` carries the same text, and the drawing
capability rows (`ui.windows`, `ui.draw`, `ui.menu_page`, `render.draw`, `kiln.screen`) turn unavailable with it.

The present queue. Kiln's frame is submitted on the queue the game presents from, in the same call. Kiln draws only on
a queue the game got (`vkGetDeviceQueue`, `vkGetDeviceQueue2`) whose family has graphics. Frame generation presents
from queues of its own: OptiScaler's (its nightly builds since October 2026) adds them to the game's device while it is
made, a present queue in the family with the fewest abilities that can present (a compute or transfer family on most
GPUs) for FSR frame generation, a graphics queue of Streamline's for DLSS frame generation, and its thread presents both
the game's frames and the generated ones there, through Kiln's present hook. A queue handed out while the device is
being made is a tool's (the game can only ask once its `vkCreateDevice` returned), unless the game asks for it too
afterwards; Kiln does not draw on a tool's queue, whatever its family. Until 0.2.0-dev.29.18 Kiln did not know such a
queue and drew for the game's first queue family: its submit (and ImGui's texture upload before it) went to a queue of
another family. In the player's report it failed with `VK_ERROR_UNKNOWN` and the game died right after (inbox 80; the
most likely cause, not yet confirmed on his PC). Now kiln.log says how many queues a tool handed out when the device is
made, Kiln draws nothing on them, and says so with the reason "frame generation presents the game's frames from its own
queue". Pre-overlay subscribers (kiln_ui.h) are not called for such a present either.

A present from a queue Kiln did not see the game get (another tool's patch on `vkGetDeviceQueue` that does not pass
the call on to Kiln's, say), or saw handed out only while the device was being made, does not turn the drawing off by
itself: Kiln looks it up once among the queues the game asked for when it made its device (the `VkDeviceCreateInfo`
Kiln's `vkCreateDevice` hook saw), with the real `vkGetDeviceQueue`. One of those is the game's own and is drawn on with
its own family, as before 0.2.0-dev.29.18. kiln.log says so once ("ui: the game presents with a queue Kiln did not see
it get; ... it is there: the game's own"). A queue a tool added under Kiln is not among them (OptiScaler adds create
infos for its own queues, or raises a family's count), and Kiln does not draw on it. A device Kiln did not see being
made (taken over at its swapchain) has no create info to look in: there Kiln draws only with the queues it saw handed
out, as before.

The semaphores. When Kiln's frame goes in, its submit waits on the game's semaphores and signals Kiln's own, and the
present waits on Kiln's. When it does not go in, or its submit fails, the present goes on with the game's own
semaphores, untouched: Vulkan leaves the semaphores of a submit that failed for lack of memory as they were (or
reports the device lost). `VK_ERROR_UNKNOWN` comes with no such promise, which is why Kiln checks the queue before it
submits and draws no more after any failure. A fault after Kiln's submit went in still presents on Kiln's semaphore.
Each semaphore is signalled once and waited on once (`native/services/ui/vk_present.h`, host test
`tests/vk_present_test.cpp`).

## Frame generation mods and drawing off

When `sl.interposer.dll` is loaded, or installed directly next to the game or under
`Streamline/`, Kiln automatically disables its Vulkan overlay drawing before installing
any Vulkan hooks. No first crash is needed. The log explains the choice and the override.

- `KILN_UI_OVERLAY=0`: drawing off; keep keys, native menu rows, game UI state and UI ticks.
- kiln.json `"ui": {"overlay": "off"}` (since 0.2.0-dev.29.18; the launcher's Settings, the kiln.json card, "Overlay"):
  the same as `KILN_UI_OVERLAY=0`, for Windows, where Steam's launch options cannot set an environment variable for
  the game. `"auto"`, the default, lets Kiln decide as above. Read at the game's start (top level only, not in a side
  section). `KILN_UI_OVERLAY`, where it is set, wins over it. The Mods entry then reads "Mods: the overlay's drawing is
  off: turned off in Kiln's settings (kiln.json ui.overlay)".
- `KILN_UI_OVERLAY=1`: try drawing even with Streamline or a guarded/locked record. This
  can crash; the crash guard still records this attempt.
- Unset (or another value): automatic choice, including crash-guard protection.
- `KILN_UI_DISABLE=1`: turn the whole UI service off; wins over both overlay switches.

The `KILN_UI_*` switches are environment variables; `ui.overlay` is the one kiln.json setting among them.
For Steam on Linux, set the variable in the game's launch options (`KILN_UI_OVERLAY=0 %command%`); on Windows use
`ui.overlay`. Remove the old `KILN_UI_DISABLE=1` workaround to keep keys and menus.

With the drawing off for any of these reasons, or stepping aside for Streamline or OptiScaler, kiln.log names it in one
line at the start ("ui: Kiln's overlay drawing steps aside for NVIDIA Streamline (sl.interposer.dll, a frame generation
mod): ... the Mods entry shows the game's own Mods rows ..."), and the Mods entry and the menu key never open Kiln's
menu: they toggle the entry's caption ("Mods: <reason>") in the game's own menu. A frame generation queue found during a
session names OptiScaler or Streamline in its line too when one of them is installed.

Drawing off preserves the game's Mods entry and mods' native menu rows, including scrolling,
keyboard/mouse/wheel bindings and native input subscriptions, game screen/language/crafting
state, camera reads/projection and `kiln.ui.tick`. Mod loading and audio's UI-frame drain
can therefore run at the menu. Input discovery finds the process's visible game window
without a Vulkan surface. An invisible window, wheel or screen never captures the game.

Windows and full mod screens stay closed; their Show/Open calls do not throw. The reason
is in `Ui.Status.Error` and the unavailable `ui.windows` row. HUDs, toasts, draw callbacks,
render-frame drawing, images, radial wheels and swapchain screen effects cannot draw.
`ui.draw`, `render.draw` and `kiln.screen` report unavailable with the same reason;
`kiln.ui` and `kiln.render` remain available. The camera hook is independent of drawing.

The existing full Mods page uses ImGui even though it occupies the game's Credits page
slot. In drawing-off mode the Mods action instead toggles its native menu caption to show the drawing-off reason; it does not open an empty page or take input. Full mod-manager tabs and config
windows need drawing; `ui.menu_page` reports unavailable. This fallback status and the
capability rows explain the degraded state. The length and layout of the fallback caption need a real-game check.

This mode avoids Kiln's Vulkan interception. It does not make Streamline and Kiln's drawing
compatible. Prefer automatic drawing off for frame generation; use the force-on switch only
when intentionally testing coexistence.

## When Kiln's menu does not show

Since 0.2.0-dev.30.2 the Mods entry (and the menu key) also checks that Kiln's menu reaches the screen. When it opens
Kiln's menu in the overlay (or the Mods page in the main menu) and no frame of the overlay is drawn and presented within
about a second (longer while the overlay is still making a frame, its first with ImGui and the menu starting, up to five
seconds), Kiln's menu falls back to the game's own Mods rows, as with the drawing off: the entry reads
"Mods: Kiln's overlay did not appear", nothing of Kiln's takes the game's input for the menu, and kiln.log says once:

> ui: Kiln's menu was opened, but no frame of Kiln's overlay was drawn and presented in the 1000 ms after (the game presented 40 frames through Kiln's hooks meanwhile): the Mods entry shows the game's own Mods rows instead, as with the overlay's drawing off, and opens Kiln's menu again once a frame of the overlay was presented

Another layer on the game's presents can keep Kiln's frames from the screen (reported with ReShade's Vulkan layer kept).
Once a frame of the overlay was drawn and presented since (a toast, a mod's HUD), the next click opens Kiln's menu in
the overlay again. Before 0.2.0-dev.30.2 nothing checked: the Mods entry opened a menu that could not be seen, and it
held the game's mouse and keys until Escape. Host test: `native/services/ui/tests/overlay_guard_host_test.cpp`.

## OptiScaler

[OptiScaler](https://github.com/optiscaler/OptiScaler) (an upscaler and frame generation tool; a player reported
Kiln's menu missing next to it on Nexus in November 2026) installs as a proxy DLL in the game folder. Since 0.2.0-dev.29.18 Kiln recognises it and
decides about its overlay's drawing before any Vulkan hook, from OptiScaler's own settings:

| OptiScaler.ini | Kiln's overlay drawing |
|---|---|
| `[Menu] OverlayMenu=auto` or `true` (OptiScaler's default), or no OptiScaler.ini | off, on its own, as for Streamline |
| `[FrameGen]` with an `FGInput` and an `FGOutput` other than `nofg`/`auto`, with its menu on or off | off, on its own |
| `[Menu] OverlayMenu=false`, no frame generation | on, next to OptiScaler |

Frame generation needs both keys: OptiScaler drops an input without an output, and makes none for an output without an
input. `[FrameGen] Enabled` does not matter: it only switches frame generation while it runs, and its own present queue
and stand-in swapchain are there with `Enabled=false` too.

`KILN_UI_OVERLAY=1` and `KILN_UI_OVERLAY=0` win over both, as everywhere. Drawing off keeps what
[drawing off](#frame-generation-mods-and-drawing-off) keeps: keys, the Mods entry (its caption then reads
"Mods: the overlay's drawing is off: OptiScaler's own overlay menu is on", or "... OptiScaler's frame generation is
on"), mods' native menu rows and `kiln.ui.tick`. The
capability rows and `Ui.Status.Error` carry the same reason. kiln.log says which file is OptiScaler, what its ini sets,
and how to get Kiln's overlay:

> ui: OptiScaler is installed (winmm.dll) with OverlayMenu=auto (on), no frame generation in OptiScaler.ini: it draws its own overlay in the game's presents and opens its menu with Insert, so Kiln's overlay drawing steps aside (keys and the Mods menu still work). For Kiln's overlay, set OverlayMenu=false under [Menu] in OptiScaler.ini and leave FGInput and FGOutput under [FrameGen] at auto (OptiScaler's menu and frame generation off, its upscaling on), or KILN_UI_OVERLAY=1 to try both at your own risk

With its menu off and frame generation set:

> ui: OptiScaler is installed (winmm.dll) with OverlayMenu=false, frame generation set (FGInput=upscaler, FGOutput=fsrfg) in OptiScaler.ini: with frame generation the game's frames are presented from OptiScaler's own queue and swapchain, where Kiln cannot draw, so Kiln's overlay drawing steps aside (keys and the Mods menu still work). For Kiln's overlay, set FGInput and FGOutput under [FrameGen] in OptiScaler.ini to auto (frame generation off, its upscaling stays), or KILN_UI_OVERLAY=1 to try at your own risk

Why (from OptiScaler's source: release v0.9.5, commit 90d9568d, and master 2df7ad9f of 2026-11-17, which its nightly
builds come from; not yet seen on a PC with OptiScaler):

- OptiScaler's profile for `enshrouded.exe` loads `vulkan-1.dll` in OptiScaler's own DllMain, through ntdll
  (`LdrLoadDll`, not `LoadLibraryExW`), and patches its exports there. When OptiScaler loads with the game, as a proxy
  DLL does, that is before Kiln starts at the game's entry point, and Kiln's hooks go on top of OptiScaler's.
- With its overlay menu on, OptiScaler patches the game device's `vkQueuePresentKHR` and swapchain functions when the
  device is made, taking them from `vkGetDeviceProcAddr`: on Windows that is Kiln's wrapper, so OptiScaler patches
  Kiln's own present and swapchain functions. It draws its ImGui menu in every present and replaces the present's
  wait semaphores with its own; when its menu fails it returns `VK_ERROR_OUT_OF_DATE_KHR` without presenting.
- Both menus open with **Insert**. OptiScaler subclasses the game window and hooks raw input, the keyboard state and
  the cursor; while its menu is open it keeps keyboard and mouse from the game, and from Kiln.
- Frame generation for Vulkan is in master since 2026-10-13 (and so in the nightlies), not in 0.9.5. It is made with the game's device
  whenever `[FrameGen]` names an input and an output, with OptiScaler's menu on or off: OptiScaler.ini's own line
  under `[Menu]` ("without this option OptiScaler will disable all FG features") no longer holds for it. It adds
  queues of its own to the device, the game draws into FFX's stand-in swapchain, and FFX's thread presents the game's
  frames and the generated ones from its own present queue, through the present function Kiln handed out. Kiln cannot
  draw there ([When the overlay fails during a session](#when-the-overlay-fails-during-a-session)): should frame
  generation be on in a way OptiScaler.ini does not show, Kiln sees that queue at its first draw and turns its drawing
  off for the session before it submits anything.

With `OverlayMenu=false` and no frame generation OptiScaler makes none of the device, present or window patches, so
Kiln's overlay works as it does without OptiScaler. Its upscaling still works; set it up in OptiScaler.ini instead of
its menu.

Detection, by contents and never by a name alone (`dxgi.dll`, `winmm.dll`, `version.dll` and `dbghelp.dll` are ReShade's,
ShroudForge's, EML's and Shroudtopia's names too): a file under one of OptiScaler's names (those four, `d3d12.dll`,
`wininet.dll`, `winhttp.dll`, `OptiScaler.dll`, `OptiScaler.asi`) directly in the game folder, or a loaded module with
one of those names anywhere, is read when `OptiScaler.ini` is next to it (or it is named `OptiScaler*`), and is
OptiScaler when it names its `OptiScaler.ini` (ASCII or UTF-16). A game folder without OptiScaler.ini reads no extra
file. An OptiScaler.ini with no OptiScaler DLL next to it (an uninstall's leftover) changes nothing; kiln.log says so.
OptiScaler running without any OptiScaler.ini and under a proxy name is not recognised (OptiScaler ships its ini). The
launcher recognises it the same way: the Play page names it and what Kiln's overlay does next to it, the support
report has a `optiscaler` finding with OptiScaler.ini (and OptiScaler.log when it writes one), and the Library leaves an
imported OptiScaler package out whole, as a tool that runs on its own.

What full coexistence with OptiScaler's own menu on would take, and why it is not done: Kiln would have to keep its
present hook below OptiScaler's whatever the order of patches (hook the device's own present function, as OptiScaler
does, instead of the loader's export and the `vkGetDeviceProcAddr` wrapper), follow the semaphore OptiScaler substitutes,
share the Insert key, and stay out of FFX's present thread with frame generation on. That is a rework of `vk.cpp`'s
Windows path that no test can check without OptiScaler in a real game.

OptiScaler also hooks DLSS and has its own frame limiter: Kiln's own graphics tools (frame cap, DLSS settings) step
aside while it is installed, whatever its ini says; the readout stays where Kiln's overlay draws
([graphics.md](graphics.md#living-next-to-other-tools)).

The additive window size-constraints slot after v7 is described in the [UI service reference](services/ui.md).

## Captured item detail (Since 0.2.0-dev.30.5)

`Ui.Values.ItemDetail` reads **passive captures** of text and stat numbers the game constructed
for a panel it actually built. It does not compute a view on demand for every item in a bag or
chest. First use starts capture; **then inspect the item in the game's own panel**. Uninspected
items return `Ok=false`, a reason, and no snapshot. There is no `UiDraw.ItemDetail` panel in
this release: draw a name/id and the reason yourself when no capture exists, never invented
weapon damage, armor, perks or food effects.

```csharp
// A completed visible-slot snapshot, not a server InventorySlot.ItemEntity.
UiItemDetailResult result = Ui.Values.ItemDetail(slot.ClientEntity, slot.Item,
    level: slot.Level, rarity: slot.Rarity);
if (result.Snapshot is { } detail)
    foreach (var line in detail.Lines)
        UseGameText(line.Text); // Value is optional metadata, not a number to append again.
else
    UseFallback(slot.Item, result.Reason); // Names/known slot metadata only; no computed stats.

// Latest inspected matching type variant, without a dummy entity argument:
var variant = Ui.Values.ItemDetail(itemId, level: 25, rarity: ItemRarity.Rare);
```

`ItemDetail(EntityId entity, ItemId item = default, int? level = null, ItemRarity? rarity = null)`
requires a **client presentation entity**, such as `VisibleSlot.ClientEntity`. Never pass an
arbitrary server inventory entity: numeric equality is not proof of client identity. An exact
entity query cannot borrow another instance's panel. For stacks without a client entity, use
the item-type query. `ItemDetail(ItemId item, int? level = null, ItemRarity? rarity = null)`
selects the most recently inspected matching variant. It may include that inspected instance's
gems/perks: it is not a newly calculated pristine item type. With no level/rarity constraints,
it selects the latest inspected variant of the type, not necessarily the one a mod intended.

The client main/UI thread captures during the game's ItemDetail call. Mods can read on **any
thread**, including a draw callback: reads copy immutable cached data under a mutex and never
access live item/world memory. Snapshots and their line lists are owned copies and remain valid
after replacement/eviction. `Lines` preserve construction order, with localized text and optional
computed floats from the stat-draw seam (after tooltip value providers). The first text row of
a stat draw carries `Value`; it is metadata, not another number to append to already formatted
text. Number-only draws have empty text. Not every numeric string has typed float metadata.

This is **presentation data, not gameplay authority or structured item stats**. Damage/element,
armor, food effects, gem and perk descriptions are available only to the extent the panel
constructs them as text through the captured seam. There are no typed damage/element/socket/
perk/effect fields, icons, styling, models, or reliable unlocked-perk flags when the distinction
is conveyed only by colour. Game-side line/number coverage still needs a real-client comparison.

The cache keeps **64 instances/variants, including rejected captures**, in capture order; reads
do not promote entries. A completed panel replaces the same client entity, or the same
item+level+rarity for a capture without an entity. A rejected replacement invalidates its old
capture too. Each panel is bounded to 64 rows, 4096 UTF-8 bytes per row and 32768 total bytes;
invalid/oversized/empty panels are refused whole, never silently truncated. Reasons distinguish
an uninspected/mismatched variant, an offline/no-local-player session, and a rejected panel.
World/session changes, missing client state, or no local player clear the cache when next
observed by a read or capture. **Language and other item-state changes do not automatically
invalidate cached text**; reinspect after upgrades, socket/perk changes, breakage or a language
change. Check the snapshot's session/frame/capture time when freshness matters. Already-owned
snapshots are historical data and do not disappear on world exit.

The `ui.item_detail` capability requires a measured client build and capture hooks. A missing or
disabled capability throws `KilnUnavailableException`; this is different from a cache miss.
See [the service contract](services/ui.md#captured-item-detail-since-020-dev305) for refusal codes.

Offline, `KilnHarness.UiItemDetail.Capture(item, entity, level, rarity, lines)` supplies an owned
recording; `Clear()` models a world change. Invalid recordings throw argument exceptions. The
TestKit models exact identity/variant matching, byte budgets, copy ownership and 64-entry
eviction, not the game's text builder, native capture failures, session lifecycle or renderer.

Client check: open an item's detail panel, then read its lines from a mod. Compare the same
weapon across levels/rarities and upgrades, armor, food, gems/perks, stackable and chest items;
reinspect after a language change, and leave/rejoin before reusing a saved identity. Run on both
Proton and native Windows, with a vanilla peer confirming unchanged gameplay and game panels.

## Native inventory background colours (since 0.2.17)
`Ui.Inventory.TintWhen(rule, item)` changes the game's own slot background, without drawing an overlay.
`ui.slots.tint` / `kiln.ui.tints` is client-only; registration and disposal work on any thread. Rules run on
the game's UI thread in registration order over copied `UiItemView` values; return `null` to keep the
earlier colour. Match `InventoryPage` and `SlotIndex` for stacks without an item entity. Empty slots are
also offered when no item filter is supplied. Dispose or unload to restore the earlier/vanilla colour.
The game's colour strength, rarity art, focus and input stay unchanged; a tint is never gameplay authority.
```csharp
using var tint = Ui.Inventory.TintWhen(slot =>
    slot.Item == trackedItem ? new Color(80, 180, 255, 160) : null);
```
Five consecutive exceptions remove a rule; registration shares the 128-per-mod ui.values budget.
An unmeasured game layout, missing signature or hook failure throws `KilnUnavailableException`;
a full registration budget throws `InvalidOperationException`, as the existing slot subscriptions do.

## Native chest-row buttons (since 0.2.17)
`Ui.Inventory.AddChestButton(caption, handler)` puts a game-drawn entry in a storage chest's own action
row. It is not an overlay panel, and does not borrow or simulate any vanilla input action. The game's
entry renderer handles its drawing, input and focus. Registration/disposal work on any thread; the callback
runs on the game UI thread after the chest builder returns. The `InventoryScreen.Container` is read from
the current chest, not a cached screen from a previous frame. Send it to a server half which validates
permissions and distance before acting: a client button is a wish, never authority.
```csharp
using var button = Ui.Inventory.AddChestButton("Count chest", screen =>
    channel.SendToServer(new CountChestRequest(screen.Container.Value)));
```
Capability `ui.chest.buttons`, independent native table `kiln.ui.chest` v1. There are four active buttons
total; captions must be unique among them and contain 1..128 UTF-8 bytes without NUL. They are literal text:
dispose/re-register to change the caption or localize it. Registrations also share ui.values' 128/mod limit,
and captions use its bounded permanent text store. A budget/duplicate refusal throws `InvalidOperationException`;
missing capability/layout/hooks throw `KilnUnavailableException`. Unload or five consecutive callback faults
removes a button. Commerce and factory screens, an unknown chest target, or a missing client session draw no
mod button. Actual row sizing, visual placement and controller navigation remain unverified in-game.
