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
- **XHL-Core.** XHL-Core 2.1.0 draws its own line at this place ("All mods loaded", or a red line about a problem).
  **Kiln's line takes its place**: while Kiln's line shows, XHL-Core's is not drawn. Nothing else of XHL-Core changes
  (its plugins, its Insert menu, its other hooks); it installs as it always did, whether it starts before Kiln or after.
  How: Kiln runs its own copy of the game's footer function, made from the exe file at start, so the line XHL-Core
  patches into the game's footer code is not reached (docs/re/game-ui.md "The main menu's footer"). kiln.log names
  the module that patched the footer (`ui: the main menu's footer: a module patched the game's footer call at ...`).
- **The setting** `mainMenuFooter` (on by default; Kiln's config `kiln/config/menus-client.json`, on the Mods page under
  **Kiln settings**) hides it: the game's footer is then drawn as it always was, with XHL-Core's line where XHL-Core is
  installed.
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
  "language": "auto"    // Kiln's own texts: auto (the game's text language) or a tag, "zh-CN" (Kiln's language, below)
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
  128 or 256 pixels; a picture with no level that size, such as a mod's one-level PNG icon, is scaled down to it), at most 256
  icons and 24 MiB: past that the icons not drawn for a second are dropped first and read again when drawn; while every icon
  in the cache is drawn now, a new one waits on the placeholder without being read. An icon scrolled out of its window is
  neither read nor drawn. An icon shows once the overlay has uploaded it (a frame or two; at most 8 new pictures a frame); one
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
  sharper level (twice the size drawn: 40 pixels reads the 128 level), so the magnified art stays crisp. The placeholder
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
- `KILN_UI_DISABLE=1` turns the whole UI service off (the service is then unavailable). It also turns off the progress window of the
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

## Overlay startup crash guard

Before Vulkan hooks are installed, Kiln writes a versioned record to
`kiln/state/overlay-start.guard`. It records hooks installed, instance seen, device seen,
swapchain created, and completes after the first successful present with the overlay drawn.
Each record is written (a temporary file, then a rename) before the stage goes on, and forced to the disk
on a thread of its own, never on the game's present call: a game that dies loses nothing it already
wrote.
A game that ends before that frame, however it ends, counts as an interrupted attempt (closing
the game while it is still loading does too: the cost is one start without the overlay).

On the next start only overlay drawing steps aside. Keys, native game-menu rows and
`kiln.ui.tick` remain available. The drawing capability rows in
`kiln/state/capabilities.json` carry the reason. The log says:

> ui: the game closed during the last start while Kiln's overlay was starting (stage: instance seen); the overlay's drawing stays off for this start; key bindings and the Mods menu still work

The stage varies. When detected it adds:
“NVIDIA Streamline (a frame generation mod) is installed; it wraps the same graphics calls”.
This is a likely conflict, not proof of the crash's cause.

Once a guarded start has run for a minute, the next start tries the overlay again once
(the log says so; automatic Streamline protection still takes precedence). A second interrupted attempt at the same stage keeps it off. To re-enable it, close the
game and delete `kiln/state/overlay-start.guard`. Removing a detected graphics wrapper
also permits a new attempt, as does adding or replacing one. The guard compares the paths,
sizes and the first and last 64 KiB of `sl.interposer.dll`, `dxgi.dll`, `ReShade64.dll`,
`opengl32.dll`, and `powrprof.dll` directly in the game folder or its `Streamline/` folder.
Loaded Streamline and ReShade copies also count, once per file; loaded Windows system copies
of dxgi/opengl32/powrprof do not. The game's `nvngx_dlss.dll` and leftover Streamline helpers
do not count as wrappers. It does not scan arbitrary subfolders. Older presence-only guard
records get one fresh attempt after this update. Only one wrapper-change recovery is allowed
until an overlay frame succeeds; further changes keep the failure history. Timestamp-only
changes do not grant a recovery. A same-size replacement that changes only unsampled bytes
may need the guard deleted manually. Unknown/unreadable records permit normal startup. File errors are logged once
and do not prevent startup. This guard does not make Streamline and the overlay compatible.


## Frame generation mods and drawing off

When `sl.interposer.dll` is loaded, or installed directly next to the game or under
`Streamline/`, Kiln automatically disables its Vulkan overlay drawing before installing
any Vulkan hooks. No first crash is needed. The log explains the choice and the override.

- `KILN_UI_OVERLAY=0`: drawing off; keep keys, native menu rows, game UI state and UI ticks.
- `KILN_UI_OVERLAY=1`: try drawing even with Streamline or a guarded/locked record. This
  can crash; the crash guard still records this attempt.
- Unset (or another value): automatic choice, including crash-guard protection.
- `KILN_UI_DISABLE=1`: turn the whole UI service off; wins over both overlay switches.

These are environment switches. kiln.ui does not read a kiln.json setting for them.
For Steam, set the variable in the game's launch environment using your platform's usual
method, and remove the old `KILN_UI_DISABLE=1` workaround to keep keys and menus.

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

The additive window size-constraints slot after v7 is described in the [UI service reference](services/ui.md).
