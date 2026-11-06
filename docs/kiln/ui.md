# Kiln.Ui: overlay windows, HUD, toasts, key bindings, the Kiln menu

The client-side UI for mods (Forge: Screens, GUI overlays, toasts, KeyMappings, the Mods screen). Native: `kiln.ui`
(`native/core/abi/services/kiln_ui.h`), which draws Dear ImGui 1.93 into the game's Vulkan presents and takes the game
window's input, and hooks the game's own UI (its screens, its main and pause menus, its crafting menus). Managed:
`managed/services/Kiln.Ui`. RE: `docs/re/client-render.md` (overlay, input), `docs/re/game-ui.md` (the game's UI).
Samples: `mods/samples/UiSample` (overlay), `mods/samples/MenuButtons` (game menus, a screen, crafting). Test:
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

## HUD and toasts
```csharp
Hud.Show("mymod.status", () => $"temp {_temp:F1}", HudAnchor.TopRight, Color.Orange);  // text computed per frame
var line = Hud.Show("mymod.msg", "static text");  line.Text = "changed";  line.Dispose();
Hud.Toast("Saved!", ToastKind.Success, seconds: 3);  // any thread
```
`Ui.Frame += ui => …` runs every overlay frame for custom drawing.

## Key bindings
```csharp
var b = KeyBindings.Register("mymod.toggle", "F8", () => w.Toggle(), "Toggle my window");
b.OnRelease = …;  b.IsDown;  b.TakePresses();  b.Consume = false;  b.WhileCapturing = true;
```
- Keys are written like `F8`, `Ctrl+K`, `Shift+Alt+F2`, `Insert`, `Numpad5`, `Backquote`, `None`.
- The player's choice is kept in `kiln/config/keybinds.json` (`{"bindings": {"mymod.toggle": "F9"}}`). The Kiln menu's
  **Keys** tab remaps keys by press: click the key, press the new one; Escape cancels.
- Handlers run on the game's main thread (the window thread) when the key goes down, but not while a Kiln text field
  has focus, and not while a Kiln screen has the input unless `WhileCapturing` is set.
- A matching binding keeps the key from the game (`Consume`, default true), including its raw-input press.
- **A key that types** (a letter, digit, punctuation or Space, without Ctrl or Alt) fires only during play or while a Kiln screen is open. The
  game's own text fields (chat, a rename, the main menu's boxes) don't tell Kiln they have the keyboard, so a tree bound to K used to open inside
  chat and take the rest of the line. `b.OnGameScreens = true` fires it on every game screen too. Function keys and Ctrl/Alt chords always
  fire. Without the game UI hooks (an unknown build) the screen isn't known and they fire as before.
- `kiln.menu` (default **Insert**) opens the Kiln menu. Its toggle is native, so it works without the managed host.
  Escape closes the menu.

## The Kiln menu
Tabs:
- **Mods**: every mod Kiln found, with version, side, state and errors. A **Config** button appears for every mod with
  a registered config (a page generated from its schema, `ConfigScreens.cs`; server configs are read-only while
  connected) or a page registered with `Ui.AddConfigPage(modId, ui => …)`; **Kiln settings** edits Kiln's own configs.
- **Network**: Kiln.Network's status, the server's Kiln version, warnings and mods, and clients and channels when hosting.
- **Keys**: every binding, with remap and reset.
- **Overlay**: resolution, scale, frame cost, error, a test toast.

If no managed code subscribed to the overlay, the native side draws a minimal Kiln window saying the managed host
is not running.

## C (native plugins)
`kiln_ui.h` (later additions, check `header.size`): `screen`, `subscribe_screen`, `subscribe_ui_tick`,
`menu_add` / `menu_set` (`KILN_UI_MENU_MAIN` / `PAUSE`), `push_style` / `pop_style`, `image_*`, `combo`,
`input_multiline`, `set_hover_capture`, and work at present under the overlay: `subscribe_pre_overlay`, `set_pre_overlay_demand`,
`add_swapchain_usage` (kiln.screen draws its full-screen effects there: [services/screen.md](services/screen.md)). `kiln.ui.tick` (the UI frame as a tick; Kiln.Host loads the mods with it) and
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
- `KILN_UI_DISABLE=1` turns the overlay off (the service is then unavailable).
- The game-UI hooks cost one check per UI frame and per menu-entry call (a return-address compare); entries draw only
  while their menu is open. `KILN_NO_MENU_LOAD=1` keeps the mods' load at the first world step.
