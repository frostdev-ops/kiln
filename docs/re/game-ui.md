# The game's own UI (client 26404966): screens, menus, crafting menus, fonts

Client Steam build 26404966 (`enshrouded.exe` sha256 `ce6fdba6…`). RVAs are this build's. Sources: Ghidra 12.1.2
headless decompiles of an analysed copy of the exe, python/capstone byte scans (string and call cross references),
the game's reflection (`tools/kfc types`), the game's UI data decoded offline (`tools/kfc decode` of
`keen::FbUiBundle` 46e14df6-… and `keen::UiFontResource` 9538a125-…), ShroudForge 1.7.6/1.7.7 (MIT, its native main-menu
entry), and a real client ("Runtime" below). Symbols: `sigdb/symbols/client.d/ui.json`. Service:
`native/services/ui/game.cpp`; usage: `docs/kiln/ui.md` "The game's own UI".

Tags: **[dis]** read from the disassembly / decompile (static, proven for this build), **[refl]** the game's
reflection, **[data]** the game's UI data, **[sf]** ShroudForge's shipped implementation, **[rt]** seen at run time,
**[hyp]** hypothesis (not proven).

## 1. Architecture: immediate-mode UI built every frame
- The UI is **code-driven immediate mode** ("GameUi"), not data-driven layouts: each frame, functions like
  `doPlayStateUi()` / `doCraftingMenu` / `doNpcInteractionWindow` (their names are window-id strings in the exe) open
  frames by name or hash, lay out children, draw text and images and ask for input. Persistent per-widget state lives in
  a hash-keyed store (`0x11a0610(frame, u64 hash, size, ctor, 1)` returns the state block; `0x11bacb0(state)` = "just
  created", i.e. the widget appeared this frame). **[dis]**
- The data side is configuration only: `keen::FbUiBundle` (81976-byte struct, one resource) holds styles, colours,
  textures, loca tags, sounds and lists for every menu (`menu.crafting.recipes` is the crafting menu's tab layout,
  §5). **[refl] [data]**
- App start creates the `GameUiSystem` (`0xa43f90`, "[app] Could not create GameUiSystem"; stored at gameApp+0x5b0)
  with an update callback, **GameUiFrame `0xbd0eb0`** (`lea rax, [rip+…]` at `0x85325d`, desc+0xc0 → system+0xf60).
  The system's update `0xa4fa60` calls it as `fn(system+0x10, p2)`. **[dis]**
- GameUiFrame builds a frame context on its stack (`0xa3c910(ctx, sys10, p2)`) and publishes it in the global
  **UiApp `0x283f4a8`** for the frame (`DAT_14283f4a8 = &ctx`), then clears it at the end (`0xa50d60(0)`). Every UI
  function reads `*(UiApp)`: `+0xc8` is the FbUiBundle config, `+0xd0` the persistent **GameUiData**. UiApp is only
  valid while GameUiFrame runs (content-data saw it null in a client-world Pre). **[dis]**
- Thread: one UI thread builds every frame in every state, worlds or not (the main menu has no world step). It is the
  game's main thread (managed thread 2, which also steps the client world). **[rt]**

## 2. State: which screen is open
| What | Where | Evidence |
|---|---|---|
| GameUiData | `*(sys10 + 0x48)` (GameUiFrame's first argument; `0xa4bf60` reads it into the ctx) = `*(ctx + 0xd0)` | [dis] |
| GameUi state | `*(GameUiData + 0)` | [dis] |
| **keen::UiState** (u8) | state `+0xc`: 0 None, 1 Preload (title), 2 PreGame (main menu), 3 Load, 4 LoreIntro, 5 LoreOutro, 6 Credits, 7 Play | [refl] enum; [dis] GameUiFrame's `switch (*(state+0xc))` at `0xbd137b`: 1 → `0xc6d090` (title screen window), 2 → `0xc69f70` (PreGame: "[GameUi] Entered PreGame", main menu `0xc2a320`), 3 → `0xc1a930` (loading), 4/5 → lore screens, 6 → `0xb81a40` (credits), 7 → `0xc602f0` (PlayStateUi) |
| Play data | state `+0x38` | [dis] PlayStateUi `0xc602f0` |
| **keen::HudScreenType** (u8) | play `+0x1cd8`; `+0x1cd9` the screen to return to | [refl] enum (54 values: 1 InGame, 2 SystemMenu, 3 SettingsMenu, 4 InGameMenu, 5 Map, 6 Journal, 7 Character, 8 PlayerCrafting, 9 Backpack, 10 SkillTree, 22 StorageChest, 25 StationBasedCrafting, 26 NpcDialog, 27 NpcCrafting, 28 FactoryCrafting, 43 TextChat, 51 NoUi, …); [dis] PlayStateUi returns at once for 0x33 (NoUi) and 0; the system menu returns unless it is 2 |
| Station workshop (u32) | play `+0x2e48`: the **keen::WorkshopId** of the crafting station in use (**[rt]**: the Workbench gave 0x4da88f73 here and as the builder's argument; first read as an entity id); `+0x2c50` set for NPC crafting | [dis] PlayStateUi keys the station's UI state by it; the HUD dispatcher passes it to the crafting builder for 25/27 |
| Text language | `*(GameUiData + 0x33ce8)` = LocaPool, `+0xd8` keen::LanguageId (docs/re/content.md 8.1) | [dis] the UI text getter `0xaff910` |

- Screen changes are **queued commands**, not direct writes: `toggleHudScreen 0xd74770(ui, type)` appends
  `{u8 1, u8 type}` to a command buffer at `*(GameUiData+0x33ce0)` ("[GameUi] toggleHudScreen to %k"); 85 callers.
  The game applies it later; the HUD screen byte above is what the UI draws. **[dis]**
- The HUD screen dispatcher `0xbe86f0(type, mode)` draws the full-screen menus: 2 → the system menu `0xcd55b0`,
  3 → settings `0xcd7970`, 25/27 → the crafting builder for the station's workshop; for the tabbed in-game menu it
  maps the screen to a tab (5 Map → tab 1, 6 Journal → 2, 7 Character → 5, 8 PlayerCrafting → 3, 9 Backpack → 4,
  10 SkillTree → 6, 44 Social → 0; tab in GameUiData `+0x31e88`) and draws that tab (crafting = `0xb80050`). **[dis]**
- Kiln reads these after each GameUiFrame (hook) and reports `KilnUiScreen`: ui_state, hud_screen, the station's
  workshop, the crafting menu (§5), language, UI thread, frame. **[dis] [rt]**
- Seen at run time: title Preload → PreGame (main menu) → None → Load → Play (HUD 0, then 1 InGame); Escape = 2;
  the tabbed menu (inventory, its crafting and map tabs) = **4** (the crafting tab is told apart by the crafting
  builder running); M = 5 (Map); a chest = 22; the Workbench = 25; Return to Main Menu = Load → None → PreGame. **[rt]**

## 3. Menus and their entries
### The main menu (PreGame landing page)
- PreGame `0xc69f70` → main menu window `0xc2a320` ("pregameMainMenuWindow", "mainMenuEntries"). Its entries are drawn
  by **MainMenuEntry `0xc2cbb0(state, desc)`** (only caller: this builder, 10 calls), `desc` = 32 bytes
  `{const char* text; u64 len; u64 fnv64(text); u8 options[8]}`; returns true when the entry is activated. **[dis] [sf]**
- Entries in order (config tags at FbUiBundle offsets; the strings are loca): Play `+0x5f90` (→ PreGame page 8),
  a link `+0x6030` ("%s <img external_link>"), **Settings** `+0x4c70` (→ page 5; drawn with "%s <img attention_dot>"
  when there is news), `+0x6000`, `+0x5ff0` (conditional), `+0x6010` (→ page 6), Quit `+0x6020` (→ `0xd71220`, the
  game's quit). A second layout branch (`+0x5fa0`…) has its own Settings entry. **[dis]**
- PreGame pages: `0xd49980(pregame, page)` ("[GameUi] PreGame: Go to %k", page at `+0x24c`); the main menu's own
  sub-pages `0xd497e0` (Landing 0 / CharacterSelection 1 / CharacterCreation 2, at `+0x4c`). **[dis]**
  Corrected on 26430625 (see "Main-menu pages" below): the `+0x6010` entry's page 6 is **Credits**;
  MainMenuEntry's first argument is `pregame+8`, not the PreGame block; the sub-page byte is `pregame+0x54`.

### The system (pause) menu
- `0xcd55b0` ("SystemMenu", "systemMenuContent", "menuLayouter"; "[GameUi] System Menu opened"), drawn while the HUD
  screen is 2. Entries go through **SystemMenuEntry `0xcb4710(text*, u8 1, u8 attention)`** (17 call sites in 4
  menus: `0xbba4b0`, `0xc65ae0`, the system menu, `0xcefa30`); `text` = 24 bytes `{ptr, len, fnv64}`; returns true on
  activation (and plays the click sound `cfg+0x124e0`). **[dis]**
- Entries: Resume `+0x6e60` (→ toggleHudScreen 1), **Settings** `+0x4c70` (→ sub-state 1), `+0x73b0` (4),
  `+0x7600` (2), `+0x9fa0` (3, conditional), a toggle `+0x8470`/`+0x8480`, `+0x5f20` (5, conditional), `+0xaf90`
  (→ screen 40 RedeemCommerceProducts), `+0xaf70` (→ 39 TwitchDrops), spacing, `+0x6e70` (→ `0xd6d480`, leave to the
  main menu), `+0x6e90` (→ `0xd71220`, quit), then the build number ("%s %u"). **[dis]**

### How an entry works (focus, gamepad, keyboard)
- An entry renderer opens a child frame, registers it with the layouter's navigation (`0xa41ed0(ui, frame)`), asks
  for hover / focus (`0xa4d1f0`), builds the confirm action hint (`0xb240b0(…, 0, 1)`), reads the activation
  (`0xa470d0`), draws the label (`0xd0d2d0(text, state, p2)`) and the attention marker. Gamepad and keyboard
  navigation is therefore the layouter's: an entry drawn through the same renderer inside the same layout is focusable
  and activatable like the game's own. **[dis]**; **[sf]** ShroudForge's "ShroudForge" entry behaves so in 1.7.6.
- The text's third field is FNV-1a 64 over the bytes (sign-extended, like the game's own producer; the empty text
  carries the offset basis 0xcbf29ce484222325 in `0xaff910`). It is the widget identity: a unique label gives a unique
  entry. **[dis] [sf]**
- Text lifetime: the game passes loca strings and frame-allocator strings; the UI may keep the pointer until the frame
  renders **[hyp]**, so Kiln interns labels for the process.

### Layout and scrolling (client 26430625, static only, nothing of it run)
Asked for a scrolling main and pause menu (2026-11-13: many rows before Settings push Quit off a 1080p screen). RVAs
of 5d9ef2f1. Helper: `kx.py` (copied from the NoClip research) over the exe as a file.
- **No cursor.** An entry is a frame of the game's retained layout tree, not a row at a cursor: SystemMenuEntry
  `0xcb47f0` opens a child frame of the current parent (`0xa3cbd0(&frame, 0)`; the frame handle at `+8`), sizes it
  (`0x11cf360(frame, {1.0, 0})`: full width, height from content), registers it for navigation (`0xa41fb0(UiApp,
  frame)`), asks for hover/focus (`0xa4d2d0`, `0xa471b0`), draws the label (`0xd0d3b0`) and closes the frame
  (`0xa40080`). Positions are computed by the layouter from the tree, so neither the row height nor the list's top is
  a number the entry code reads. **[dis]**
- **The pause menu's tree** (builder around `0xcd60f9`): a frame "systemMenuContent" (`0xa3cf70`, hash
  0xca8fd50ed1226434), inside it "menuLayouter" (`0xa3cb10`, hash 0x84a61178ec189a93; a value 25 or 40 picked by the byte at
  `rbp+0xb60`, use not traced; `0xa47740` with {0xe6000000, 1.0}), inside that a column frame (`0xa3c790`) whose width is the UI scale
  (`0xd46090(UiApp, 1.4, 0.8)`) times 300 units, then the entries: Resume (`call 0xcb47f0` at `0xcd63ab`), Settings
  (`0xcd6546`, the site Kiln draws before), and the rest down to Quit. **[dis]**
- **The game's own scroll view exists:** `keen::GameUiScrollingLayout`. Begin **`0xa3e020(layout*, u8 dl, state*,
  r9, params*)`**: `layout` is the caller's storage (at least 0x470 bytes: an outer frame at `+8`, a content frame at
  `+0x38`, the copied parameters from `+0x58`), `params` a `GameUiScrollingLayoutParameterData` (0x184 bytes copied;
  fields named in the reflection strings: enableScrollbar, forceScrollbar, doScissorWhenScrolling,
  useAutoScrolling, scrollingHotKeyMask, showScrollHint, scrollbarAlignment, scrollBar), `state` a per-call-site block
  from the UI's hash-keyed state store (`0x11a06e0(store, hash, size, ctor, 1)`; the credits use hash
  0xeb56c10a5766ad62, size 4). It names the outer frame "GameUiScrollingLayout" and, vertically, moves the content
  frame by the scroll offset (`0x11cf380(content, {0, offset})`). End **`0xa40160(layout*)`** (85 callers). The
  credits (`0xb81ecc` … `0xb82a3e`) and the recipe info panel ("infoScroller", `0xbb5f66`) use it; the parameter
  block is built by `0xa3eeb0` (defaults) and `0xa3df20` plus the scrollbar look copied from the UI bundle
  (`UiApp+0xc8` at `+0xfa84…+0xfb04`). Frame state `keen::UiFrameScrollState` {scrollOffset, scrollAreaSize}.
  **[dis] [refl]**
- **Not found statically:** what bounds the menu column's height (a scroll view needs a parent of fixed height; the
  column may grow with its content), how the scroll view follows the gamepad/keyboard focus (useAutoScrolling is the
  candidate), and the meaning of `dl` (1 in every caller looked at) and `r9`.
- **Cost of a scrolling menu** (route "the game's scroll view"): Kiln's renderer hooks already see the first entry
  (Resume / Play) and the last (Quit) by their return addresses; Kiln would begin a GameUiScrollingLayout before the
  first and end it after the last, with its own storage, state block and a parameter block built as the credits build
  theirs. About 2 days of further static work (the parameter block, the column's height, the focus) and several client
  sessions; risk: the game's layout asserts are fatal (`0xcb4c38`: "SmallDynamicArray is out of elements", then
  `0x8a0d90`), so a wrong nesting ends the game on the first frame, and no run-time check before the first frame can
  rule that out. A game update moves every one of these functions (each needs a signature).

### What Kiln does (native injection, shipped)
- One inline hook on each renderer. When the call comes from the **Settings** entry's call site (return address =
  `ui/MainMenuSettingsA` / `B`, both layout branches; `ui/SystemMenuSettings`), Kiln first calls the original renderer
  for each of its entries (main menu: a copy of the Settings descriptor with Kiln's text, so the same options/style),
  then the game's Settings entry. Activations are queued and delivered after the UI frame. ShroudForge 1.7.6 inserted
  its entry at the same point by patching both call sites (MIT, credited in game.cpp). **[dis] [rt]**
- With ShroudForge's native runtime in the process (1.7.6 to 1.7.9, also hosted by Kiln) the two Settings calls no
  longer reach the renderer from the game: each `call` enters SF's `draw_with_mods` through a near thunk, and that
  function calls the renderer (its own row when its Modloader is there, then the Settings descriptor). The renderer's
  return address is then in `kfc-runtime.dll`; the game's (`ui/MainMenuSettingsA` / `B`) is on the stack above SF's
  frame (in Kiln's build of 1.7.9: `sub rsp, 0x480` after five pushes, so 0x4a8 bytes above the renderer's return
  address **[dis]** of that DLL). Kiln's hook takes such a call for the Settings entry too (`menu_site.h`: a caller
  outside the game's image with a Settings return address within 4 KiB above it), once per UI frame. A real client
  with SF mods had no Kiln entries in the main menu before this **[rt]**; with the fix the same client shows "Mods" and the
  SET tools entry there again **[rt]** (2026-11-11, the pinned 1.7.9 build).
- Localisation: Kiln resolves the label for the game's LanguageId (§2) from the mod's per-language texts.

## 4. Fonts, colours, textures
- Fonts: `keen::UiFontResource` (one resource, 9538a125-9b82-4857-b04e-9b448b3d0a33): `fonts` = BlobArray of
  `keen::TtfFontResource` (32 B: a ResourceContentReference to a **TTF blob**; 28 fonts, 60 KB … 11 MB: Latin and CJK),
  `languageGroups` (24 B: language mask, glyph sets (fontIndex, scale, rune ranges, variant mask Bold/Italic),
  metrics, isDefault). 6 language groups; the default one (28 Latin/Cyrillic/Greek languages) uses fonts 0 / 4 / 24 / 8
  = **Harmonique Display** Regular / Italic / Bold / Bold Italic (OpenType with CFF outlines, 85 KB each) plus CJK and
  Thai fallbacks (Noto). The default group's regular font is index 0 (84896 bytes, content blob in enshrouded_053.dat).
  **[refl] [data]**
- Kiln's game-look windows load that TTF at run time through kiln.assets (`resource_list` + `resource_read` +
  `blob_read`) into ImGui; no game file is copied or shipped. **[rt]** ("the game's UI font loaded (font 0 of 28, 84896 bytes)")
- Colours (FbUiBundle `menu.*`, `keen::UiColor` packed ABGR): tileBackgroundColor #363246 (a 169), tabHighlightColor
  #caac54, tileHighlightOutlineColor #f5c558, textTabRowBgHighlightColor #896828, tabLabelIdleColor #c0cdd5 (a 171),
  basicWidgetColor #131313 (a 90), globalColors.attention #ffe06b, warning #f26e6e. **[data]** Kiln's "game" ImGui
  style uses them.
- Menu button art: `FbUiMenuButton` (standalone / in-group nine-slice textures, background, destructive, glow,
  ornament; label style `UiCharacterStyle`). Not used by Kiln (native entries draw with the game's own; ImGui windows
  use colours only). **[refl] [data]**
- UI textures: `keen::UiTextureResource` through the UI texture cache `0xa4e5f0` (docs/re/content.md §7); kiln.assets
  serves new ones. **[dis]**

## 5. Crafting menus
- Builder **CraftingMenu `0xb80050(u64 packed, i32 select)`**: `packed` byte 0 = 1 for a station's menu (HUD screens
  25 / 27 pass `{1, …, workshop}`), byte 1 = 1 for the journal's recipe book ("[GameUi] opened Crafting Menu from
  Journal"), the high dword = the station's workshop id **[rt]** (0 = the player's own crafting: "opened Personal Crafting Menu";
  "opened NPC Crafting Menu" when play+0x2c50 is set). Called every frame while a crafting menu shows. **[dis]**
- Its persistent state (hash 0xc07230f951cf77e0, 0x588 B) holds the selection; at the end of every build it stores the
  selected recipe id in GameUiData `+0x31ed8` (own crafting) or `*(GameUiData+0x31ed0)` (a station's per-entity
  state). Kiln reads it right after the builder. **[dis]**
- List rebuild `0xda5760` (on open and when the lists change) calls the category lookup **`0xd5ade0(lists, …,
  workshop id, …)`** four times; content-data hooks it (docs/re/content.md §7). It is also called from `0xbb4600` (a
  journal recipe overview). **[dis]**
- Configuration: `FbUiBundle.menu.crafting.recipes` = `keen::FbUiCraftingRecipeCollection`: `workshops` (48 workshop
  ids, id 0 = the player's own), `trees` (per workshop a BlobArray of **tabs** = `keen::FbUiCraftingRecipeGroup`
  0x38 B {groupId GUID, icon UiTexture, label LocaTagReference, sets}), each tab's **subcategories** =
  `keen::FbUiCraftingRecipeSet` 0x28 B {label, recipeSetId, entries: BlobArray<RecipeId>}; the first subcategory of a
  tab has a zero label (no header). `maxRecipeGroupCount` 15 / `maxRecipeSetCount` 28 are exactly the largest counts
  in the data (workshop 0: 6 tabs). **[refl] [data]** The game sizes its menu by these maxima **[hyp]**: Kiln's
  additions stay within them.
- Extension point (shipped): `kiln.craftmenu` v1, implemented inside content-data's existing `0xd5ade0` hook (no
  second detour): Kiln tabs and subcategories are copies of a workshop's tab array in near memory with Kiln's entries
  appended; the tree header is pointed at the copy; labels are new LocaTagReference GUIDs whose tag ids content's loca
  hooks answer. Info panels next to the menu are Kiln overlay windows (they know station, workshop and selected
  recipe). **[dis] [rt]**

## 5b. Main-menu pages (client 26430625, static analysis only)
Nothing in this section has been seen running. Source: `kiln-wave/dev22/re/REPORT-main-menu.md` (Ghidra headless,
capstone, `tools/kfc`), re-checked in the disassembly where Kiln relies on it. RVAs are 26430625's (functions sit
+0xe0 from the 26404966 RVAs above). Symbols: `ui.json` feature `ui.menu_page`. Kiln's use: `native/services/ui/menu_page.cpp`.

**The PreGame block.** 0x250 bytes in the UI's hash-keyed state store (key `0xca244aa5e0cf3615`). The store frees
blocks a frame does not touch, so a pointer to it is only good inside the frame. `+0x24c` i32 current page;
`+0x54` u8 main-menu sub-page; `+0xd0` / `+0xec` current / target camera view (`keen::FbUiCharacterView`).
There is no previous page, no target page and no transition timer. **[dis]**

**Pages** (names from 0xd41440; the enum is not reflected). The PreGame frame 0xc6a050 calls the page's draw
function from a jump table each UI frame:

| id | page | draw | camera | leaves by |
|---|---|---|---|---|
| 3 | MainMenu | 0xc2a400(pregame) | mainMenu shot | entries call GoTo(8 / 5 / 6) |
| 5 | Settings | 0xbe87d0(3, 0) | postMainMenu (close-up) | returns false → GoTo(3) |
| 6 | Credits | 0xb81b20() `bool(void)` | postMainMenu | returns false → GoTo(3) |
| 8 | PlaySelection | 0xc5d720(pregame) | postMainMenu | back: sound + page = 3 |

Others: 0 HdrCalibration, 1 EULA, 2 PS5 preset, 4 UserFeedback, 7 News, 9 SessionStarter, 10 PlayJoin,
11 BaseSharingBrowser; ids above 11 draw nothing.

- **PreGameGoTo 0xd49a60** `(pregame, page)`: compares (page 10, 3 → 8, page 5), writes `+0x24c`, logs
  "[GameUi] PreGame: Go to %k". No queue, no sound. **[dis]**
- **Main menu entries** call `MainMenuEntry(pregame+8, desc)` then, when it returns true, `GoTo(pregame, page)` at once,
  inside the main menu's draw: Settings 0xc2b460 / 0xc2babe (→ 5), Credits 0xc2bcb3 (→ 6). **[dis]**
- **Credits slot.** `call 0xb81b20` at 0xc6aac0 returns to 0xc6aac5 (`ui/PreGameCreditsRet`), which jumps to
  0xc6aa9b: `test al,al; jne skip; GoTo(rsi = pregame, 3)`. Credits has one other caller (0xbd1578, the end-game
  credits state). **[dis]**
- **Camera.** The view switch after the page switch picks the target from the page (5, 6, 8, 10 → postMainMenu) and the
  camera eases there (about 97 % in 0.5 s). Page functions read no transition state: a new page draws on the next frame
  while the camera moves. **[dis]**
- **Invite.** With a pending invite (data+0x12f20) and a page above 2 the frame writes page 3 directly (0xc6a894) and
  logs "[GameUi] going from %k to MainMenu to accept invite"; no page function runs that frame. **[dis]**
- **Back sound.** PlaySelection's back: `UiPlaySound 0xa4f310(*UiApp, &copy of cfg+0x124f0)`
  (`sounds.menu.entryCancel`). **[dis]**
- **Canvas** (UI system update 0xa4fb40; GameUiFrame gets `system+0x10`): `system+0x30/+0x34` canvas in UI units
  (1920x1080 times the scale at `+0xf48`, grown along the longer side), `+0x38..+0x44` viewport x, y, w, h (floats,
  pixels), `+0x48` pixels per UI unit = min(w / 1920s, h / 1080s). **[dis]**
- **Padding.** PlaySelection uses `FbUiPreGame.playMenusPadding` = 80 / 50 / 80 / 50 units **[data]**, raised to the
  safe-area insets **[dis]**. Kiln uses the 80 / 50 values (the bundle field is not read at run time).
- **Error popup.** "errorMessageWindow" (layer 31) draws while the byte at `sys10 + 0x120 + 0x550` is set
  (0xa4c050 returns `sys10 + 0x120`; 0xbae4b0 tests `+0x550`). **[dis]**
- **Title look.** `namedStyles.menu.header1`: bold, upper case, tracking 0.05, white, 46 units. The bold face is the
  default language group's glyph set with `keen::UiFontVariantMask` bit 0 (Bold): font 24 of 28. **[data] [refl]**

**What Kiln does** (feature `ui.menu_page`; needs the MainMenuEntry hook): inside its MainMenuEntry hook, when its own
MODS entry returns true, it checks `*(pregame+0x24c) == 3` and calls `GoTo(pregame, 6)` there, as the game does for its
entries. A hook on Credits answers only calls returning to `ui/PreGameCreditsRet` while Kiln's page is open: true
(stay), or false once to leave. Nothing is written into the PreGame block.

### Client checklist (needs the real game)
1. Click MODS: the camera glides to the Settings close-up; the main menu is gone on the next frame; no credits text,
   music or sound; the Kiln page shows. Log: `ui: Kiln page in the main menu opened by the MODS entry`.
2. Escape, then gamepad B, then the Back hint: each goes back once, with the back sound; the camera glides back; no
   quit dialog. Log: `closed (left; the game goes back to its main menu)`.
3. After coming back: a main-menu entry has the focus and works with keyboard and pad, as after Settings.
4. The game's own Credits after using MODS: the real credits show and leave normally.
5. A Steam invite while the page is open: the log shows "going from Credits to MainMenu to accept invite" and
   `closed (the game left the page by itself)`; the Kiln page disappears.
6. 1920x1080, 2560x1440, 3440x1440, 1920x1200: the title's left edge and the hint bar line up with PlaySelection's
   header and tiles (80 / 50 units in).
7. Settings, frame by frame: is the game's page fully drawn on its first frame while the camera still moves? (Kiln
   draws from the first frame the page shows.)
8. The mouse cursor is visible on the page and the game's cursor does not fight Kiln's.
9. Back to the main menu from a world, then MODS again: it still works (the PreGame block is new).
10. With ShroudForge's main-menu entry installed: both entries work.
11. Insert in the main menu opens the page (`opened by the menu key`); Insert on the page leaves; Insert in a world
    opens the stand-alone menu.
12. A game error popup (for example, pull the network while joining): the page steps aside and the popup takes the
    mouse, keys and pad; Escape closes the popup, not the page.
13. The page stays readable over the real close-up shot (it has no background of its own).
14. Kill Kiln.Ui's drawing (a build without the managed host): the page leaves by itself after about a second and MODS
    then opens the stand-alone menu (`no host drew it for a while`).

## 6. Other strings worth knowing
"[GameUi] … opened" log lines exist for every menu (Backpack, Map, Journal, Skill Tree, Settings, Social, NPC dialog,
…): the game logs screen changes itself, at level 3. Window ids: `titleScreenWindow`, `pregameMainMenuWindow`,
`PlayStateBaseWindow`, `recipeListWindow`, `ingredientSelectionWindow`, `itemViewerWindow`, `ContextMenuWindow`,
`textInputWindow`, `tooltipWindow`. **[dis]**

## 7. Not done / open
- Native custom screens (a mod screen drawn with the game's widgets): needs the layouter / text / image calls
  (`0xa3ca30` frames, `0xb1d930`+`0xb25080` text, `0xa48c10` images, …) as an API; Kiln ships ImGui screens in the
  game's look instead.
- Buttons inside the game's crafting panel (next to "Craft"): the recipe detail layout `0xc75580` / `0xc79ec0` was not
  mapped; Kiln's panels sit beside the menu.
- Gamepad: the game loads XInput at run time (XInput1_4 / 1_3 / 9_1_0, `XInputGetState` by name) **[dis]** (strings).
  Kiln hooks that export after it loads: while Kiln has the input the game gets an idle pad and ImGui gets the pad
  (navigation; B = Escape). **[hyp]** until tried with a pad; native entries use the game's own pad navigation.
- (answered at run time: the tabbed menu stays HUD screen 4 on every tab; M opens the map as 5.)

## Runtime (GE-Proton11-11, 1920x1280)
Kiln deployed with MenuButtons, ui-test and NewItems; a LOCAL save, world slot 0x0002
(world1.ksd), Offline. ui-test **25 PASS, 0 FAIL**.
```
ui: game UI hooked: screens on, main menu on, pause menu on, crafting on, language on
ui: gamepad: xinput1_3.dll XInputGetState hooked (idle for the game while Kiln has the input)
ui: game screen None/0 -> Preload/0
[host] lifecycle: loading the mods in the game's menu (UI frame 1)
ui-test: 13 PASS, 0 FAIL at load
ui-test: PASS 600 UI ticks (UI thread 2)
ui: menu entry 'Kiln' activated            (mouse, main menu)
ui: the game's UI font loaded (font 0 of 28, 84896 bytes)
ui: menu entry 'ui-test' activated         (keyboard: Down + Enter, main menu)
saves: world loaded: slot 0x0002 (world1.ksd) …
ui: menu entry 'ui-test' activated         (pause menu)
kiln.craftmenu: 2 tab(s) and 2 subcategories in the crafting menu (1 of 1 header(s) written; limits 15 tabs, 28 per tab)
new content: the crafting menu lists 5 new recipe(s) (3 of 3 subcategory list(s) rewritten)
menubuttons: recipe 0x222fcad4 at workshop 0x00000000      (the crafting panel's button)
menubuttons: screen InGame -> Crafting (Play/StationBasedCrafting, … workshop 0x4da88f73, recipe 0x8e78753c)
ui-test summary: 25 PASS, 0 FAIL, nothing waiting; UI thread 2
```
- Main menu and pause menu: Kiln's entries drawn by the game's renderers in the game's look (pause menu: with the
  game's attention marker), above Settings; mouse clicks and keyboard focus (Down/Enter) activate them; the game's
  own entries (Settings, Return to Main Menu, Quit) work unchanged. **[rt]**
- A Kiln screen (game look, the game's own Harmonique Display font) opened from either menu or F7: list, slider, text
  field (one character at a time), image work; Escape closes the screen only (the game's pause menu stays, in game
  the pause menu does not open); keys work afterwards (no stuck keys seen). **[rt]**
- Crafting: Kiln tabs and subcategories drawn by the game's crafting menu with their labels; the info panel follows
  the selected recipe and its button takes the click while the game keeps the keyboard. **[rt]**
- The UI thread is the game's main thread (managed thread 2, the one that also steps the client world). **[rt]**
- Not covered: a gamepad (none attached), language switching (game in English), a joined remote server, HDR.
