# The mod API: the game UI, HUD and keys

Part of the mod API guides ([api.md](api.md) is the index). Here: menus, screens, HUD, key bindings, presses as they come,
looks and themes, item icons, values in the game's own UI, the game's inventory slots, tooltip lines, outlines. Signatures
are the common ones; the XML docs and the generated API reference ([docs-map.md](docs-map.md)) have the rest.

## Game UI, HUD, keys
`Ui.Menus.Add(MenuLocation.MainMenu|PauseMenu, text, onClick)`, `Ui.Screen(title, ui => { ... }, options)` →
`Open/Close/Toggle`, `Ui.Crafting.AddTab / AddSubcategory / AddPanel`, `Ui.Screens.Current`, `Hud.Show(id, text)`,
`Hud.Toast(text)`, `KeyBindings.Register(id, "F8", action, description)`, `UiText.Of(("en", ...), ("de", ...))`.
Key names: `F8`, `Ctrl+K`, `Shift+Alt+F2`, `Numpad5`, `None`; dev.27+ also the mouse: `Mouse4`, `Mouse5`, `MouseMiddle`,
`WheelUp`, `WheelDown`, `WheelLeft`, `WheelRight` with modifiers (`Ctrl+WheelUp`, `Shift+Mouse4`; one press per notch, a
notch has no release). Left and right click cannot be bound (`Parse` throws, `TryParse` is false, as it is for every
mouse name on a Kiln before dev.27). A taken press is kept from the game (Ctrl+WheelUp does not zoom the camera).
dev.28+: every key has a name that reads back (`Key223` for a key with no name, `Oem8`, `IntlBackslash`); punctuation
names such as `Backquote` mean the key at that place on any layout. dev.29.3+: one press never fires `OnPress` twice.
dev.27+: with many entries the pause menu scrolls (`ui.menu_scroll`); where a menu cannot scroll (the main menu) and
more than 3 mods add entries, theirs are listed on Kiln's Mods page instead, where `OnClick` runs the same way (the
player's `modEntries` setting decides). Don't assume your entry is drawn in the game's menu. dev.29+: the pause menu's
Auto shows every mod's entry whenever it can scroll.

**Presses as they come (dev.29.19+, Kiln.Ui; not yet seen in a mod in a real game).** For a mod's own "press a key"
field or shortcut layer; an action the player should remap stays a `KeyBindings.Register` binding.
`KeyInput.Listen(p => ..., KeyListenFlags.Mouse)` (the default flag adds the middle and side buttons and the wheel)
returns a `KeyListener` (`Ok`, `Reason`, `Mouse`, `Dispose()`; it also ends at unload). The handler gets every
`KeyPress` (`Down`, `Repeat`, `Captured`, `TextInput`, `Mods`, `IsMouse`, `Code`, `Chord`) on the game's window thread
before the game: keep it short, return true to keep the press from the game. Where there is no overlay (a dedicated
server) the listener is not `Ok`, with a `Reason`; it never throws for that. `KeyInput.PressCode(vk, scan)` and
`p.Chord.ToString()` are what the Keys page stores. Don't read Kiln's internals (`KilnText`, `UiNative`, `KeyNames`) by
reflection: `KilnLanguage` and `KeyInput` are the public members for them (`docs/kiln/ui.md`, the XHL-Core table).

**The pointer and drag and drop (dev.30.3+, Kiln.Ui; not yet seen in a real game).** In a draw callback: `ui.Pointer`
(`Vector2?`, overlay pixels, `DrawRect`'s space), `ui.ButtonHeld(UiMouseButton.Left)`, and ImGui's drag and drop:
`ui.DragSource("stack", payload, preview)` right after a widget, `ui.DropTarget("stack")` right after another (the payload
text once, on release; else null). Kinds are the mod's own; payloads are text up to 4096 bytes. `SlotClick.Pointer` is
the press position in the same pixels (`Ui.Inventory.OnClick`). Page `docs/kiln/ui.md`.

**The HUD's ammunition and spell lists (dev.30.6+, mod API 0.2.15, Kiln.Ui; not yet seen in a real game).**
`Ui.AmmoList.Provide(AmmoListKind.Arrow | AmmoListKind.Spell, ctx => rows, new AmmoListOptions { Mode = AmmoListMode.Append })`
returns an `AmmoListProvider` (`Refresh()`, `Dispose()`; it also ends at unload). The provider runs on the game's UI
thread when the list's `AmmoListContext` changes (`Kind`, `Held`, `GameRows`, `Session`, `Revision`) and returns
`AmmoListRow(item, count)` rows in its order; `Replace` hides the game's rows (and since dev.31.21 keeps its last rows and
the cursor over the game's new build until Kiln publishes for it, at most 1 s, GitHub #151). `Ui.AmmoList.Picked += pick => ...` gets
a selection wish (`AmmoListPick`: `Kind`, `Item`, `Count`) for this mod's rows the backpack does not hold: send it to the
server half, which validates it and swaps the stack in. Rows `ui.ammo.list` and `ui.ammo.pick` (`Available`,
`PicksAvailable`); test kit `kiln.AmmoList`. Page `docs/kiln/ui.md`.

**Looks and themes (dev.29.16+; not yet seen in a real game).** `UiStyle.Player` (the look the player chose) is the
default for `Ui.Window`, `Ui.Screen` and crafting panels; before, windows had Kiln's ember look. `ui.Palette` reads the
look's colours by role. `Ui.Themes.Define(name, new UiThemeSpec { ... })` makes a theme for your own windows
(`Theme =` on their options, or `using (ui.Theme(theme))`); `UiThemeSpec.OfferToPlayer = true` lets the player pick it
for Kiln's UI. A mod
never restyles other windows. `Ui.LoadFont(path)` and `ui.ModFont(font)` give your windows a font. Wiki
[looks](https://frostdev.io/kiln/wiki/ui-looks); page `docs/kiln/ui.md`.

**Item icons and names (dev.29.15+; not yet seen on screen).** `Ui.ItemImage(id)`, `ui.ItemIcon(...)`,
`Ui.ItemIconsAvailable` draw the icon the game draws for an item (a new item shows its base item's icon until its own is
served); dev.29.16+ `ItemIconTrimmed`, `ImageTrimmed`, `ImagePart`. A server has no icons. The overlay holds up to 960
images. Crafting panels show at production stations only with `CraftingPanelOptions.AtFactories`; there read
`CraftingScreen.Station`, not `Workshop`.

**Values in the game's own UI (dev.28+, client; not confirmed in a real game).** `Ui.Values` replaces the numbers and bars
the game draws for an item (`ItemBar`, `SlotBar`, `HeldBar`) and the local player's health, stamina and mana (`Hud`):
the picture only, never the values. `Ui.Values.Slots()` / `SubscribeSlots` read the slots drawn each frame;
dev.29.11+ `GemForgeCandidates()` the forge's GEMS list. Rows `ui.values.*`, `ui.slots.inspect`.

**Graphics (dev.29.18+, client).** `kiln/state/graphics.json` (schema `schema/state-graphics.schema.json`) says what
Kiln's graphics tools and the upscaler behind the game's FSR 3 do. Mods have no managed call for it; native plugins read
`graphics_status` in kiln_ui.h.
`Ui.Available` is false on a dedicated server. dev.20+: `WindowBlock`, `HudLineBlock`, `KeyBindingBlock` and the equivalent
JSON sections bind config and safe `game.*` values without draw code: [blocks.md](blocks.md#ui-as-data).
dev.20+: `StatusLines.Add(() => cachedText)` / `Set("state", text)` report mod status when asked, including on servers.
A source may run on a worker: use a tick-produced snapshot, never game state or tick-held locks. dev.21+: the menu and the
dashboard never wait for a source. They show the lines read last and refresh on a worker, so a new line appears a moment later.
**Placing screens and windows (0.2.0-dev.31.11+, mod API 0.2.20; tested offline, not yet seen in a game).** Positions are
fractions of the viewport (0..1) of the centre unless a pivot says otherwise; sizes are pixels at UI scale 1.
`UiScreenOptions.Position` = where a screen opens (null: the middle, as before). `screen.MoveTo(x, y, pivotX = 0.5f,
pivotY = 0.5f)` / `screen.Resize(w, h)` (also on `UiWindow`; any thread) apply once on the next frame it draws (ImGui
`Always` for that frame); on a closed screen they wait for its next open, on a removed or `FullScreen` one they do
nothing. `screen.Placement` (`UiPlacement`: `X`, `Y`, `Width`, `Height`, `Left`, `Top`, `Right`, `Bottom`) is where it was
drawn last. `RememberPlacement = true` (+ optional `PlacementId`, default the title) keeps the player's place and size per
mod and id in `ui-placement.json` beside `keybinds.json` (config folder), across game starts. Precedence: MoveTo/Resize >
remembered > options; everything Kiln places stays inside the viewport. Beside another mod's screen:
`MoveTo(other.Right, other.Y, pivotX: 0)` with the other's `Placement` shared through `Mods.Send`. Tests:
`kiln.UiPlacement.Frame(screen)`, `PlayerPlaces`, `Remembered`, `Restart` (docs/kiln/ui.md "Placing screens and windows").
**Docking windows (0.2.0-dev.31.16+, mod API 0.2.23, GitHub issue 149; tested offline, not yet seen in a game).** Prefer
it over `Mods.Send` + `MoveTo` for two windows that stay together. The target opts in with `UiWindowOptions.AcceptDock =
true` (+ `PlacementId`); another mod gets `Ui.FindWindow(modId, placementId)` (a `UiWindowHandle`, null when that window
is not there or does not accept docks; a name, so it survives the target's reload, compared by name also with `==`:
`Available`, `Open`, `Placement`) and calls `window.DockTo(handle, DockSide.Left/Right/Top/Bottom, offset = 0)` (offset:
pixels at UI scale 1 along the shared edge), `Undock()`, `DockedTo`, event `DockChanged` (`UiDockChange`: `DockedTo`,
`Side`, `Offset`, `ByPlayer`). Kiln moves the pair together in the same frame on the render thread (drag of either one,
a resize of the shared edge, `MoveTo` of either), keeps it inside the viewport (a dock that becomes active uses the
other side when it does not fit; a group too big for it keeps the target inside), Shift+drag moves one alone and undocks
it (`ByPlayer`), a drop within 16 px of the old side (or the side it was shown on) docks again; a docked window opened
while its existing target is closed waits up to 250 ms for it (one key press for both: no flash), and a window already
shown never jumps when the other one opens. `DockTo` throws for itself and for loops. A remembering docked window keeps
the player's own position, not the dock's. Only windows (not `UiScreen`) dock. Call `DockTo` when the window opens (the
other mod may load later). `ui.WindowMovedByUser` / `WindowResizedByUser`: the player drags or resizes the current
window this frame; `ui.HeldMods` (`KeyMods`, ImGui's io this frame, cleared on focus loss) is the Shift/Ctrl/Alt state:
no `KeyInput` listener needed. Tests: `kiln.UiPlacement.FrameWindows()`, `PlayerDrags(window, x, y, shift)`. Samples:
`mods/samples/DockBook` + `DockNotes` (docs/kiln/ui.md "Docking windows").
dev.22+: `new UiScreenOptions { FullScreen = true }` is a whole-display screen (no title bar, not movable); draw your own
layout with `ui.FontSize(36)`, `ui.BeginChild(id, w, h, UiChildFlags.NavFlattened)`, `ui.Cursor` / `ui.SetCursor`, `ui.LastItem`,
`ui.Selectable(label, selected, width, height)`, `ui.DrawRect(UiLayer.Window, ...)`, `ui.PushColor` / `PushVar` and
`ui.KeyPressed(UiKey.Tab, claim: true)` (docs/kiln/ui.md "Layout for full screens"); `ui.HasLayout` is false on an older kiln.ui.
The Kiln menu (Insert, the MODS entries) lists every engine's mods; `Ui.AddConfigPage` pages show inline in its details.
A UI `command` runs in this game's registry; it does not send chat to a joined server. Use `castAbility` / `applyEffect`
with the same mod on the server for server-side actions. Old callbacks, queued edits and commands cannot outlive their mod load.

**dev.20+ game text:** `Texts.Get/GetPlain(tag)` in `Kiln.Content` resolves English loca. A server needs a client catalog
from the same game build (`kiln catalog build --side client`, local `kiln/catalog/client.json`). `Texts.Status` explains a
missing catalog; `Preload` loads ahead of time. dev.21+: while another thread loads the catalog (`Preload`, or a first call),
`Texts.Get` returns `null` at once instead of waiting; `Texts.Status` says "still loading". Catalogs contain game data and
must not ship in a mod.

## The game's inventory slots, item detail and outlines (dev.30+)
Kiln.Ui, client only. Everything here happens in one player's game: the server and other players see nothing unless your mod
sends a message (`Network`). A click is a wish, never authority: send the item's id to your server half and check there. Page
`docs/kiln/ui.md` "The game's inventory slots"; samples `SlotMarks`. Not yet seen in a real game.
```csharp
// Ctrl+click marks an item; the game never sees the press (no pick-up)
Ui.Inventory.OnClick(new SlotClickOptions { Modifiers = SlotModifiers.Ctrl }, click =>
{
    if (click.ItemEntity.Value == 0) return;                    // an empty slot, or a plain stack (no item entity)
    Ui.Inventory.Mark(click.ItemEntity, new SlotMark(new Color(235, 60, 50)));
});
using var tint = Ui.Inventory.MarkWhen(s => s.Rarity == ItemRarity.Legendary ? new SlotMark(Color.Red, SlotMarkStyle.Fill) : null);
using var panel = Ui.Inventory.AddPanel("Auto Loot", (ui, screen) =>          // docked beside the backpack or chest slots
{
    if (ui.Button("Salvage marked")) channel.SendToServer(new SalvageRequest(screen.Container.Value));
}, new InventoryPanelOptions { Screens = InventoryScreens.Chest, Width = 240 });
Ui.Tooltip.AddLines(myItem, (in UiValueContext c, in UiItemView v) =>          // lines in the game's own item view
    UiTooltipLines.With(new TooltipLine("+12 % fire damage", TooltipColor.Positive)));
Ui.Tooltip.AddValues(myItem, (in UiValueContext c, in UiItemView v) => UiTooltipScale.By(1.25f));   // scales its numbers
```
- **`Ui.Inventory`**: `OnClick(options, handler)` (`Button`, exact `Modifiers`, `Take` keeps the press from the game, `Pad` for
  a controller press on the `Selected` slot), `Mark` / `Unmark` / `ClearMarks` and `MarkWhen(rule)` (a mark follows the item;
  `SlotMark(Color, Style = Border/Fill/Corner, Thickness)`), `Hovered`, `Selected` (30.5, the controller's focus),
  `AddPanel(title, draw, options)`, `Screen` and `ScreenChanged` (an `InventoryScreen`: `Kind`, `Hud`, `Container`, the
  chest's entity, which is the server's id for a chest the server sent). `InventoryScreens` is `Backpack`, `Chest` or `All`.
  Marks and clicks work on the backpack, the inventory menu, chests, stations and NPC crafting, never the play view's
  hotbar for clicks. Rows `ui.slots.click`, `ui.slots.inspect`, `ui.inventory.panels`, `ui.screens`.
  dev.31.9+ (#117; read from the game's code, not yet seen in a game): marks under the game's item menu (right-click a
  slot) and other popups are left out (row `ui.popups`), and `GameScreen.PopupOpen` (`Ui.Screens.Current`, `UiTick`) is
  true while one is open, for a mod that draws over the game's screens itself.
- **The item's identity.** `SlotClick.ItemEntity` / `VisibleSlot.ClientEntity` is this game's entity, only for items with an
  item entity of their own (weapons, tools, armour, cans); a plain stack has none. `VisibleSlot.ServerEntity` (30.5) is the
  server's id for the local player's slots and the open container. `VisibleSlot.Rarity` and `Level` (30.5; also on
  `SlotClick`) are what the slot's tooltip shows, so a `MarkWhen` rule can tell a Legendary staff from an Uncommon one
  without `Items.Read`. `SlotClick.Pointer` is the press position in `UiDraw`'s pixels.
- **`Ui.Tooltip`** draws lines into the game's **own** item detail view (`TooltipPlace.End` or `AfterStats`; colours
  `TooltipColor`: the rarity colours, `Positive`, `Negative`, `Muted`) and scales its stats numbers (`AddValues`, a picture on
  this client only). Lines also list in Kiln's Item details window, which is the fallback where the game's view cannot take
  them. 16 lines per view for all providers. Rows `ui.values.tooltip_lines`, `ui.tooltip.lines`, `ui.tooltip.values`.
- **`Ui.Values.ItemDetail(entity | item, level?, rarity?)`** (30.5, row `ui.item_detail`) reads the text and numbers the game
  built for an item panel the player **has inspected**: `UiItemDetailResult` (`Ok`, `Reason`, `Snapshot` with `Lines`). It
  computes nothing: an uninspected item answers `Ok = false`. Any thread; presentation data, never gameplay authority.
  Pass a `VisibleSlot.ClientEntity`, never a server entity.
- **Controller keys** (30.5): `KeyBindings.Register("fav.mark", "Pad:LS", ...)`. Names `Pad:A` `B` `X` `Y` `LB` `RB` `LT`
  `RT` `LS` `RS` `Start` `Back` `Up` `Down` `Left` `Right`, and combinations with a shoulder or trigger held (`Pad:LB+A`).
  They never mix with Ctrl, Shift or Alt; an older Kiln's `KeyChord.TryParse` says false. `KeyChord.IsPad`.
- **Outlines** (30.3+): `Ui.World.Outline(entity, new OutlineOptions { Color, Pulse, Priority, Duration, ThroughWalls = true })`
  lights an entity's model with the game's highlight and returns a `WorldOutline` to dispose. `ThroughWalls` (30.5, row
  `ui.world_outline.through_walls`, `Ui.World.ThroughWallsAvailable`) adds Kiln's own box silhouette that shows through walls;
  asking while the row is off throws `KilnUnavailableException`. `docs/kiln/render.md`.
