# Kiln.Ui: overlay windows, HUD, toasts, key bindings, the Kiln menu

The client-side UI for mods (Forge: Screens, GUI overlays, toasts, KeyMappings, the Mods screen). Native: `kiln.ui`
(`native/core/abi/services/kiln_ui.h`), which draws Dear ImGui 1.93 into the game's Vulkan presents and takes the game
window's input, and hooks the game's own UI (its screens, its main and pause menus, its crafting menus). Managed:
`managed/services/Kiln.Ui`. RE: `docs/re/client-render.md` (overlay, input), `docs/re/game-ui.md` (the game's UI).
Samples: `mods/samples/UiSample` (overlay), `mods/samples/MenuButtons` (game menus, a screen, crafting),
`mods/samples/ValueBars` (values in the game's own UI). Test:
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
  menu where it scrolls, below; otherwise as `Few`), `Few` (a menu draws them while it has at most 3 visible ones, else
  none of them), `Always` (every one, as before 0.2.0-dev.27) or `Never`.
- **The pause menu scrolls (an experiment, feature `ui.menu_scroll`).** With more than 3 mods' entries kiln.ui wraps
  the pause menu's column in the game's own scroll view (the one the credits use: mouse wheel, clipping, following the
  keyboard and gamepad focus); with fewer it draws the menu exactly as the game does. Begin and end sit at the
  column's own open and close in the game's builder (`native/services/ui/menu_scroll.h`). A crash guard writes
  `kiln/state/menu-scroll.trying` before the first scrolled frame of a start and removes it once the scrolled menu has
  drawn 48 frames in that process (a wrong nesting would have ended the game by then; quitting afterwards is fine); a
  start that finds it (or cannot read it) keeps scrolling off (`kiln/state/menu-scroll.off`, naming the Kiln version
  and game build, written before the marker goes; another version or build tries once more; delete the file to try
  again) and tells the player with a toast. A fault in Kiln's own calls closes what it opened, in order, and turns
  scrolling off; when even that fails the game ends on purpose with a kiln.log line, rather than a moment later in its
  own menu code. kiln.log says when it is on (`ui: the pause menu can scroll`, `ui: the pause menu scrolls: ...`
  with the sizes it saw) and when it switched itself off and why. The main menu does not scroll. Kiln's
  own **Mods** entry is always drawn. Every mod's entry of the menu the player can open where they are (the main
  menu, or the pause menu in a world) is listed on the Mods page under **Menu entries**; choosing one there closes the
  Kiln menu and runs the entry's `onClick` exactly as the menu would (on the UI thread after the next frame). Nothing
  changes for a mod: `Ui.Menus.Add` returns the entry as before, whether a menu draws it or not. The 3 is a guess
  (no row height is known for a 1080p screen); the guided client check `MenuRowsLeaveQuitOnScreen` asks for it.

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
- Looks: `UiStyle.Game` (the game's menu colours and **the game's own UI font**, which Kiln reads from the game's
  resources at run time and never ships) or `UiStyle.Kiln` (Kiln's ember theme, branding/palette.json). Inside any
  window: `using (ui.Look(UiStyle.Game)) { … }`.
- Widgets: everything in `UiDraw` (below) plus `Image` / `ImageButton` (PNG via `Ui.LoadImage`), `Combo`,
  `InputMultiline`, `Space`, `WindowRect`, `CloseScreen`.
- Open from the game's menus (`Ui.Menus`), a key binding, a command or code (any thread).
- `FullScreen = true`: the whole display, no title bar, not movable or resizable (Width/Height are ignored); draw your own
  title and layout with the calls in "Layout for full screens". Escape still closes it.
- Screens are Kiln overlay windows (ImGui), not game widgets. Gamepad: while Kiln has the input the game sees an idle
  pad (XInputGetState hooked) and the pad drives ImGui's navigation (d-pad / left stick move, A activates, LB/RB
  switch tabs, B closes the top screen like Escape). Native menu entries use the game's own pad navigation. Under Proton the mouse works wherever the game leaves the pointer free (main menu, pause menu, crafting
  and inventory menus); see "Windows" below for in-game pointer lock.

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

## Values in the game's own UI (dev.28)
`Ui.Values` replaces what the game itself draws for an item and for the local player: the number and bar of the item
detail view, the bar on an inventory slot, the held item's water indicator and the HUD's health, stamina and mana. It
also adds lines for an item to Kiln's **Item details** window and reads the slots the game drew. **Picture only:** the
server keeps the real values (no healing, no spending, no capacity); a player without the mod and every vanilla client
see the game unchanged. Native: in-core `kiln.ui.values` (`native/core/abi/services/kiln_ui_values.h`). Sample:
`mods/samples/ValueBars`. Guided checks: `05a-c1` to `05a-c5` in `mods/tests/client-pass/Dev28ValuesUi.cs`.

| Member | Draws | Current / Max | Edit | Row (held until) |
|---|---|---|---|---|
| `ItemBar(item, provider)` | detail view number and bar, its label | what the game prints (water: **charges**; at most 2,147,483,647) | fraction, numbers, label | `ui.values.detail` (`05a-c1`) |
| `SlotBar(item, provider)` | the slot bar (only its fraction shows) | raw fuel units | fraction, numbers | `ui.values.slot` (`05a-c1`) |
| `HeldBar(provider)` | the held item's indicator | raw fuel units | numbers, Max above 0 | `ui.values.held` (`05a-c3`) |
| `Hud(HudValue, provider)` | health / stamina / mana pair | the vital's units | numbers, Max above 0 | `ui.values.hud` (`05a-c2`) |
| `TooltipLines(item, provider)`, `OpenTooltipDetails()`, `CloseTooltipDetails()` | lines in Kiln's Item details window | – | up to 16 lines per detail view | `ui.values.tooltip_lines` (`05a-c4-viewer`) |
| `Slots()`, `SubscribeSlots(onFrame)` | (reads) the slots of the last completed UI frame | – | – | `ui.slots.inspect` (`05a-c5`) |

`item` is an `ItemId`, `default` for every item. Every row is off on a server, on a build where its game code did not
resolve, and **until its guided check passed** (the client gate: [services/caps.md](services/caps.md#the-client-gate-dev28));
the pass and a preview profile switch them on.

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
that is off throws `KilnUnavailableException` naming it, a refusal comes back as a result. Not done: lines in the game's
own tooltip (3-5 days of layout work), rows in the inventory header or character sheet, pet bars, values for a chest a
player only opened (`InstanceData` Holder sync covers the holder only).

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
  - free drawing: `DrawText`, `DrawRect`, `MeasureText`;
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
- `ui.HasLayout` is false on an older kiln.ui: each call then falls back to the older one or does nothing.

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
```csharp
var b = KeyBindings.Register("mymod.toggle", "F8", () => w.Toggle(), "Toggle my window");
b.OnRelease = …;  b.IsDown;  b.TakePresses();  b.Consume = false;  b.WhileCapturing = true;
```
- Keys are written like `F8`, `Ctrl+K`, `Shift+Alt+F2`, `Insert`, `Numpad5`, `Backquote`, `None`.
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

## The Kiln menu (the mod manager)
A page in the game's look (its colours and its UI font), opened by the menu key (Insert), the main menu's and the
pause menu's **Mods** entry; Escape (gamepad B) or the **Back** hint goes back. A title, a tab row of sections, a footer
of key hints. Tab / Shift+Tab or LB / RB switch sections; arrows or the d-pad move; Enter or A chooses; Ctrl+F jumps to the search box.
- **Mods**: three columns.
  - Left: **All mods**, **Needs attention** (only when a mod failed or its folder is missing), then one row per engine
    that has mods (Kiln, native DLL mods, Kiln plugins, chain DLLs, EMF, EML, XHL, ShroudForge, SET, Greeneye, Flame,
    Shroudtopia, Enshwick, shroudlink, Ember) with its count and a mark (green fine, grey idle, red a problem), and
    **Kiln settings** (Kiln's own configs) when there are any.
  - Middle: the mods of that row (name, version, a state chip: Loaded, Waiting, Off, Failed, Not loaded, Missing; the
    engine when mixed), a search box that filters as you type (every word must match the name, id or engine), the engine's
    one-line state when it matters ("ShroudForge: runtime hosted by Kiln.", "EMF: stepped aside for the real EMF",
    "Ember: mods are found but cannot run yet."), and an empty state that says what to do.
  - Right: the selected mod: name, id, version, state, engine and side chips, the full reason or error and what to do
    about it, the engine, the folder; for Kiln mods also the load order, what it needs, its status lines
    (`StatusLines`) and its settings inline: the page generated from its config schema (`ConfigScreens.cs`; server
    configs are read-only while connected) and the pages it registered with `Ui.AddConfigPage(modId, ui => …)`.
    On the generated page a number with a `[Range]` is a slider with a number field beside it, where a value is typed;
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

Where the list comes from (`ModCatalog.cs`, internal; usable on a dedicated server, which has no menu):
`Mods.All` for Kiln mods; `<game>/kiln/state/` for the rest: `boot.json` (chain DLLs, Kiln plugins, the profile in use),
`native.json`, and each engine plugin's file (`emf.json`, `eml.json`, `set.json`, `greeneye.json`, `flame.json`,
`shroudtopia.json`, `shroudlink.json`, `ember.json`; contracts.md 5.9). ShroudForge's packages come from the profile and
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
word `off-in-shroudforge`, with where to switch it on (Play in the launcher for Kiln's copy; the ShroudForge group or
ShroudForge's own Modloader for the player's own folder), and the EML engine line counts them as a problem; on, a mod
with a startup part shows `applied` when the record lists it, else Waiting `unconfirmed`. Kiln's EML copies are not
repeated in the ShroudForge group; a player's own legacy EML folder there (no `extended.mod.json`) is listed in it,
switched off, with the switch that writes ShroudForge's file. Enshwick's mods
come from the profile and mods folder, with loader state from `enshwick.json`; a folder there that another kind owns
(a profile entry of another kind, a `native-plugin.ini`, a `mod.json`, or Kiln's placement marker of another kind) is
not listed as Enshwick's. XHL companions (the profile's `mods.xhl`, and folders with XHL-Core's `native-plugin.ini` in
`<game>/mods/`) are their own group: "placed for XHL-Core; Kiln does not load it", with the XHL-Core prerequisite, never
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
- `KILN_UI_DISABLE=1` turns the overlay off (the service is then unavailable). It also turns off the progress window of the
  early step (`kiln.progress`, [eml.md](eml.md#what-you-see-while-it-runs)).
- The game-UI hooks cost one check per UI frame and per menu-entry call (a return-address compare); entries draw only
  while their menu is open. `KILN_NO_MENU_LOAD=1` keeps the mods' load at the first world step.

## The contract (dev.27)

Kiln.Ui follows [the API contract](modding/contract.md) as far as it applies. It is a game-client service: `Ui` (with
`Ui.Screens`, `Ui.Menus`, `Ui.Crafting`), `Hud`, `Render`, `KeyBindings`, `UiDraw`, `RenderDraw` and `WorldDraw` are
`[ClientOnly]`; on a dedicated server their calls draw nothing and answer null or false, as before.

- **Capabilities.** `kiln.ui`, `kiln.render` and the client's sigdb features (`ui.screens`, `ui.main_menu`,
  `ui.menu_page`, `ui.menu_page_sound`, `ui.system_menu`, `ui.crafting`, `ui.error_popup`, `ui.language`,
  `render.camera`), lowered by Kiln.Ui when their service is not loaded. No member throws for a missing capability, so
  none carries `[RequiresCapability]`; the `Available` properties answer as before.
- **Threads.** `UiDraw` is `[RenderThread]` (it exists only inside a draw callback). `Ui.Screens.Current`,
  `Render.Camera`, `Hud.Toast`, `HudLine.Text`, `UiWindow.Open`, `MenuEntry.Text` and `StatusLines.Set` are
  `[AnyThread]`, as their docs said.
- **Events.** `Ui.Screens.Changed` (the game's UI thread, at once) and `ScreenChangedEvent` (the bus, tick thread, one
  step later) are two deliveries of one native callback, not two sources; both stay.
