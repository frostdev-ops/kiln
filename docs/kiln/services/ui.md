# kiln.ui window size constraints

See [the overlay and managed UI guide](../ui.md) for the other window calls.

`KilnUiApi.set_next_window_size_constraints(min_w, min_h, max_w, max_h)` is appended
at the end of the native table, after `subscribe_key_ex`; the service version stays 7.
It forwards to Dear ImGui `SetNextWindowSizeConstraints` for the next `begin`, in
screen pixels. Call it inside a frame callback before `begin`, every frame that
needs constraints. Like the neighbouring window setters it does nothing outside
a drawing frame, including `KILN_UI_OVERLAY=0` and kiln.json `"ui": {"overlay": "off"}`.

Readers must check `header.size >= offsetof(KilnUiApi, set_next_window_size_constraints) +
sizeof(ui->set_next_window_size_constraints)` and that the pointer is non-null.
Do not require the size of today's entire table. Existing offsets stay unchanged: on x64 the previous table was 888 bytes, the new member is at byte offset 888, and the extended table is 896 bytes (`subscribe_key_ex` remains at 880).
The managed internal mirror appends `TableConstraints` after the frozen `Table7`
and checks the member end before reading the pointer. Window setters are internal
to `Kiln.Ui`, so this adds no public C# API or tick-thread analyzer entry.

With `KILN_UI_WINDOW_ALWAYS_AUTO_RESIZE`, equal minimum and maximum width fixes
the width while height follows content between `min_h` and `max_h`. EMF windows
use `(width, 0, width, frame.height * 0.85f)`: later rows grow the window and content
past the cap scrolls. Older overlays retain the previous appearing-only size call.

Mods' screen and window placement (0.2.0-dev.31.11, mod API 0.2.20: `UiScreenOptions.Position`, `MoveTo`, `Resize`,
`RememberPlacement`) adds no slot: Kiln.Ui calls `set_next_window_pos` / `set_next_window_size` and reads `window_rect`, so
the sizes it sets go through ImGui's minimum size and these constraints like any other
([the guide](../ui.md#placing-screens-and-windows-since-020-dev3111-mod-api-0220)).

## Kiln's line in the main menu's footer (0.2.0-dev.29.11)

`KilnUiApi.menu_footer(int32_t mods, uint32_t flags)` is appended after `set_next_window_size_constraints`; the service
version stays 7. On x64 it is at byte offset 896 and the table is 904 bytes. Readers check `header.size >=
offsetof(KilnUiApi, menu_footer) + sizeof(ui->menu_footer)` and that the pointer is non-null; the managed mirror is
`UiNative.TableFooter`.

Kiln.Ui calls it with the count of mods loaded this start (< 0: not known yet, no line) and `KILN_UI_FOOTER_SHOWN`
unless the player hid the line (setting `mainMenuFooter`). kiln.ui then draws "Kiln &lt;version&gt; - &lt;N&gt; Mods
Loaded" above the game's build number, in place of XHL-Core's footer line ([the guide](../ui.md#kilns-line-in-the-main-menus-footer-since-020-dev2911)).
Any thread; the next UI frame follows it. A native mod has no reason to call it.

## Overlay images from pixels and a factory screen's station (0.2.0-dev.29.15)

Three more slots are appended after `menu_footer`; the service version stays 7. On x64 `image_load_rgba` is at byte offset
904, `screen_target` at 912, `image_state` at 920, and the table is 928 bytes. Readers check `header.size` through the member
they call and that its pointer is non-null; the managed mirrors are `UiNative.TableImageRgba`, `UiNative.TableScreenTarget`
and `UiNative.TableImageState`.

- `image_load_rgba(owner, rgba, width, height)`: `image_load` with pixels instead of a PNG (width * height * 4 bytes, R G B A,
  rows top down, straight alpha; 1..4096 a side; copied). The same kind of image: `image`, `image_button`, `image_free`, kiln.render's
  image draws. 0 when the overlay does not draw or the size is out of range. Kiln.Ui loads the game's item icons with it
  (kiln.assets' `texture_rgba` decodes them; [the guide](../ui.md#item-icons-and-names)).
- `screen_target()`: while the open screen is a factory screen (`hud_screen` 28, FactoryCrafting), the entity it is about
  (play data `+play.target`, 0x2c50 on client 26430625: the station's entity id), else 0. Since 0.2.0-dev.30 also on the
  two other screens PlayStateUi draws with the chest-style transfer screen `0xb9d0f0`: a storage chest (22) and 41
  (CommerceProductChest), where it is the chest's entity (kiln.ui.inventory v2's `screen_view`, `GameScreen.Target`); also 0 when the constant
  `ui/play.target` did not resolve (kiln.log says so once at start). Any thread. A factory screen runs no crafting builder, so
  `KilnUiScreen.crafting` stays 0 there; its workshop is `KilnUiScreen.interaction` (play data +0x2e48). `KilnUiScreen`
  itself does not grow: kiln.ui.values' structs embed it. A change of the target while the screen stays open counts as a
  change of the screen (subscribers are called). Kiln.Ui reports the screen as a `CraftingScreen` with `IsFactory`
  and `Station` ([the guide](../ui.md#crafting-menus)).
- `image_state(image)`: `KILN_UI_IMAGE_WAITING` (loaded, its texture not uploaded yet), `READY` (uploaded with its descriptor
  set: drawn), `FAILED` (uploaded without one: never drawn; free it), `UNKNOWN` (no such image). Any thread.

**Image budget** (`native/services/ui/image_budget.h`, every image of `image_load` and `image_load_rgba`): each image is one
texture with one descriptor set from the overlay's ImGui Vulkan pool, which holds 1024 sets (it held 64 before
0.2.0-dev.29.15). Kiln lets 960 images exist at once (freed ones count until their texture is destroyed); past that both loads
answer 0 (kiln.log says so once). ImGui keeps the other 64 for its fonts. At most 8 new textures are uploaded a frame (the
backend submits and waits for each); the rest wait for the next frames. A set the pool cannot give leaves the texture without
one: kiln.log says so once, the image's state is FAILED, and `image`, `image_button` and kiln.render's image draws draw
nothing for it (keeping its place; a button stays clickable), as they do for an image still WAITING. Vulkan errors of the
overlay's backend are logged once per error code. Kiln.Ui's item icon cache keeps at most 256 images.

## Looks, themes and fonts (0.2.0-dev.29.16)

Eight more slots are appended after `image_state`; the service version stays 7. On x64 `theme_create` is at byte offset 928,
`theme_free` 936, `push_theme` 944, `set_look` 952, `look_info` 960, `font_load` 968, `font_free` 976, `push_font_handle` 984,
and the table is 992 bytes (1008 with [the image tail](#part-of-an-image-trimmed-images-020-dev2916), 1016 with `set_texts`). Readers check `header.size` through the member they call and that its pointer is non-null; the
managed mirror is `UiNative.TableLooks`. The guide: [Looks and themes](../ui.md#looks-and-themes-020-dev2916).

- **Looks.** `push_style` takes `KILN_UI_STYLE_PLAYER` (2) too: the look the player chose (`set_look`). Windows that push no
  look are drawn in the player's colours as well (their font stays the overlay's default; a push gives a look's font). An
  older kiln.ui draws `PLAYER` as `KILN`.
- **Palettes.** A look is a palette of `KILN_UI_ROLE_*` colours (text, muted text, window, panel, frame, border, accent,
  button, header, attention, warning, ok) from which kiln.ui makes ImGui's colours (`native/services/ui/look_palette.h`,
  `style.cpp`). The game look's palette comes from the game's UI bundle at run time: `keen::FbUiBundle.menu` /
  `.globalColors` colour fields (`keen::UiColor.value`, A B G R from the high byte), their offsets from `kiln.types`
  (`type_by_name`, `type_info`, `type_field`, walking base classes) and the bundle's bytes from `kiln.assets`
  (`resource_list` with the type's hash, `resource_read`), read on a worker with the game's font at the first frame in a
  game-based look (asked again 5 s later, at most 4 times in all, while the game has not listed the bundle or the font
  resource yet). Without them (no
  `kiln.types` or `kiln.assets`, a field missing, no bundle) the game look keeps a neutral dark stand-in
  (`look::neutral`): kiln.log `ui: the game's UI colours are not available (...)` or `... loaded (N of 10 colours ...)`.
- `theme_create(owner, &KilnUiThemeDef, &handle)`: a theme (copied). `base` GAME or KILN; `roles` (by `role_mask`; R G B
  over the base's palette, the alpha is the base's), `colors` (by `color_mask`, any `KILN_UI_COLOR_*`, set after the roles),
  `vars` (by `var_mask`, `KILN_UI_VAR_*` in pixels at scale 1), `background` (the windows' opacity, < 0 the base's),
  `rounding` (every corner, < 0 the base's), `font` (a `font_load` handle, 0 the base's). `INVALID_ARGUMENT` for a short or
  null `def`, another base, a value that is not finite. Any thread. `theme_free(handle)` (`NOT_FOUND` when gone).
- `push_theme(handle)`: inside a frame callback, like `push_style`; `pop_style` pops it, and the end of the frame pops what a
  callback left. False when there is no such theme (nothing pushed). Kiln.Ui pops what a mod's draw callback left before the
  next window is drawn.
- `set_look(&KilnUiLook)`: the player's look: `look` (`KILN_UI_LOOK_GAME`, `TRANSLUCENT`, `KILN`, `THEME` with `theme`;
  another value is GAME), `background` (TRANSLUCENT's windows' opacity, clamped 0.1..1), `opacity` (ImGui's global alpha and
  the alpha of `draw_rect` / `draw_text` and their layered forms, clamped 0.3..1), `scale` (multiplies the display's scale
  for every frame: `KilnUiFrame.dpi_scale` includes it; clamped 0.5..2). Any thread; the next frame follows it. **Kiln.Ui
  only**: a call whose return address is in a native plugin (a module exporting `kiln_plugin_main`) is refused with
  `KILN_ERR_UNAVAILABLE` (logged once). A theme the look names that does not exist (yet, or any more) draws the game look.
- `look_info(&KilnUiLookInfo)`: the player's look (`look`, `theme`, `background`, `opacity`, `scale`; `look` is THEME only
  while that theme exists), whether the game's colours (`game_colors`) and font (`game_font`) are loaded, and `roles`
  (`role_count` of them): inside a frame callback the palette of the look pushed now (the player's when none is), elsewhere
  the player's. Any thread.
- `font_load(owner, data, len)`: a TrueType or OpenType font (TTF, or OTF with CFF outlines; a collection is refused),
  copied, added to ImGui's font atlas at the next frame. 0 when the overlay does not draw, the bytes are no font, or the
  budget is used (`native/services/ui/font_budget.h`: 8 fonts, 8 MiB each, 24 MiB together; kiln.log says which). A font
  takes no overlay image: the atlas is one texture, glyphs are made on demand. `font_free(handle)` drops it at the next frame.
  `push_font_handle(handle)`: inside a frame, the font at the current size until `pop_font`; false when it is not in the atlas
  (yet) or unknown.
- `KILN_UI_COLOR_COUNT` is 28: `TITLE_BG` 20, `TITLE_BG_ACTIVE` 21, `POPUP_BG` 22, `TAB` 23, `TAB_HOVERED` 24, `TAB_SELECTED`
  25, `TABLE_HEADER_BG` 26, `TEXT_SELECTED_BG` 27 are new (also for `push_color`; an older kiln.ui ignores them, so do not pop
  them there).
- Host test: `native/services/ui/tests/style_host_test.cpp` (in `tests/run.sh`) runs style.cpp on a headless ImGui context
  with a fake `kiln.types` and `kiln.assets`.

## Part of an image, trimmed images (0.2.0-dev.29.16)

Two more slots are appended after `push_font_handle`; the service version stays 7. On x64 `image_ex` is at byte offset 992,
`image_opaque_rect` 1000, and the table is 1008 bytes. Readers check `header.size` through the member they call and that its
pointer is non-null; the managed mirror is `UiNative.TableImageEx`. The guide: [Item icons](../ui.md#item-icons-and-names)
("Trimmed").

- Every image of `image_load` and `image_load_rgba` gets its **opaque bounds** when it is loaded (on the loading thread,
  before the lock): the smallest rectangle holding every pixel with alpha >= 16 (`img::kOpaqueAlpha`,
  `native/services/ui/image_rect.h`); the whole image when no pixel is that opaque. Kept with the image; nothing else changes.
- `image_ex(id, image, &KilnUiImageDraw)`: `image` / `image_button` with more say. `KilnUiImageDraw` (32 bytes: `size`,
  `flags`, `width`, `height`, `u0`, `v0`, `u1`, `v1`; set `size` first, a short or null one draws nothing): the box (0 = the
  source's pixel size), the source rectangle in texture coordinates (all 0 = the whole image; u1 < u0 flips), and
  `KILN_UI_IMAGE_DRAW_TRIM` (1: the source is the opaque bounds, the UVs are ignored) and `KILN_UI_IMAGE_DRAW_FIT` (2: the
  source keeps its aspect ratio inside the box, as large as fits, centred; the whole box is still taken). An empty `id`
  draws an image (a `Dummy` of the box with the picture on the window's draw list, so hover and tooltips cover the box;
  returns false); else an `ImageButton` of that id whose frame is the box's (FIT's spare room joins the frame padding),
  true when clicked. Inside a frame callback. Until the image is READY (`image_state`) the box is kept and nothing drawn (a
  button stays clickable). Kiln.Ui draws `ItemIconTrimmed(item, size)`, `ImageTrimmed(...)` and
  `ImageButtonTrimmed(...)` with TRIM | FIT, and `ImagePart(img, w, h, u0, v0, u1, v1)` with the UVs.
- `image_opaque_rect(image, &x, &y, &width, &height)`: the opaque bounds in pixels (any pointer may be null). False: no
  such image. Any thread. Kiln.Ui's `RenderDraw.ImageTrimmed` reads it and draws through kiln.render's `image` (which
  already takes UVs).
- Host tests: `tests/image_rect_test.cpp` (the bounds on synthetic icons with margins, fully transparent and opaque ones,
  odd sizes, faint pixels, random images against a full scan; UVs; fitting) and the image part of `style_host_test.cpp`
  (bounds kept from `image_load_rgba`, trimmed / fitted / part-of-image draws and buttons, quads and UVs on the draw list).

## Kiln's own texts and CJK glyphs (0.2.0-dev.29.16)

One more slot after `image_opaque_rect`: `set_texts` at byte offset 1008 (the table is 1016 bytes; check `header.size`
through it and its pointer). The guide: [translating-game.md](../translating-game.md).
- `set_texts(&KilnUiTexts)`: Kiln's own texts that kiln.ui draws (the Mods entry in the game's menus, Kiln's line in the main
  menu's footer, the window shown without the managed host), from `runtime/locales/<language>.json`'s `native` section, and
  `language`, the language Kiln's UI shows (a lower-case tag). `count` pairs of `keys` / `texts` (UTF-8, copied; keys
  outside `native.` ignored; at most 4096). Kiln.Ui calls it at start and whenever the language changes; the Mods entries and
  the footer's line are made again at once. Until then, and for a key it lacks, kiln.ui draws its built-in English
  (`native/services/ui/ui_text.h`). Plural keys come as `.one` / `.other`; kiln.ui takes `.one` for 1. **Kiln.Ui only**: a
  native plugin's call is refused with `KILN_ERR_UNAVAILABLE` (logged once), as for `set_look`. `INVALID_ARGUMENT` for NULL, a
  short `size`, a count over 4096 or NULL arrays with a count. `KilnUiTexts` is 40 bytes (`language` at 8, `keys` 24,
  `texts` 32).
- **Glyphs.** When that language (else the game's text language, which mods' texts follow) is Chinese, Japanese or Korean,
  kiln.ui reads the game's own font of that language group: `keen::UiFontResource` through `kiln.assets` (`resource_list`,
  `resource_read`, `blob_read`), the group whose `groupLanguages` mask has the language and is not the default group, its
  regular glyph sets (`fontVariantMask` 0) in order, each font once, at most 4 files, 16 MiB each, 24 MiB together
  (`font_budget.h`), the fields' places from `kiln.types` when it has them (else 26430625's layout,
  `native/services/ui/font_group.h`). On a worker, at the first overlay frame that needs it (asked again 5 s later, at most 4
  times, while the game has not listed the resource). The render thread then merges each file into the overlay's default
  font, the game's UI font and its bold face (`MergeMode`, the target's size, `GlyphExcludeRanges` = every rune outside the
  set's ranges), now or as those fonts arrive; another group's files replace them, a Latin language removes them (their
  sources leave the fonts; glyphs are made again as drawn). Mods' fonts get none.
- **The default group's other fonts** (0.2.0-dev.29.19). For every language, at the first overlay frame (any look), kiln.ui
  reads the default group's regular glyph sets after its first (the Latin source), each font once, only files up to 2 MiB,
  at most 4 and 4 MiB together (`font_budget.h` `admit_default`; 26430625: Noto Serif for Cyrillic, Noto Sans Thai, 577 KB;
  the CJK fallbacks are left to the CJK groups), and merges them into the same three fonts the same way, before a CJK
  group's (which waits for them, unless they failed). Kept for the process; asked again while the resource is not listed,
  as above.
- `KilnUiLookInfo.glyphs` (byte 30; it was reserved) is `KILN_UI_GLYPHS_LATIN` (0, no such language), `LOADING` (1),
  `READY` (2) or `FAILED` (3, kiln.log says why), and `glyph_language` (byte 31) the `keen::LanguageId` it is about (20 zh-CN,
  21 zh-TW, 11 ja-JP, 13 ko-KR; since 29.19 9 ru-RU, 24 bg-BG, 34 uk-UA, other Cyrillic tags 9). The language is Kiln's
  (set_texts) when its script is not Latin, else the game's when its is not (`font_group.h` `needed`). For a CJK one the
  state is its group's; for a Cyrillic one the default group's fonts' (`READY` only when they cover U+0410..U+044F).
  Kiln.Ui shows its overlay texts in English until `READY` for its language.
- Host tests (`tests/run.sh`): `ui_text_test.cpp` (the handoff, plurals, placeholders, the built-in English against
  en.json), `font_group_test.cpp` (the group, sets and exclusions from made-up resource bytes, another layout, bad data, the
  budget), `group_host_test.cpp` (style.cpp's loading on a headless ImGui context with a made-up resource and a system TTF:
  the default group's fonts and the Cyrillic state, the language's choice, the state, merging into fonts that arrive later
  in order, replacing, removing, a failed read, the layout from reflection; `KILN_TEST_CJK_FONT=<an extracted CJK font>` also checks a real CJK glyph).

## Kiln's graphics tools (0.2.0-dev.29.18)

Two slots after `set_texts`: `graphics_set` at byte offset 1016 and `graphics_status` at 1024 (the table is 1032 bytes;
check `header.size` through the member and that its pointer is non-null). Player side and kiln.json keys:
[graphics.md](../graphics.md).
- `graphics_status(&KilnUiGraphicsStatus)` (72 bytes; set `size` first): what Kiln's frame cap, DLSS settings and readout
  do now. `flags`: `KILN_UI_GFX_CAP_ACTIVE` (the cap paces frames now), `_DLSS_HOOKED` (Kiln's hooks on NVIDIA's NGX core
  are in), `_DLSS_ACTIVE` (the game upscaled with DLSS in the last two seconds), `_DLSS_RESTART` (a DLSS setting or the
  readout is on but was not at start), `_DLSS_PRESET` / `_DLSS_SCALED` (the DLSS in use got Kiln's preset / render size),
  `_COUNTING` (the frame counts are kept), `_DLSS_LIMITED` (the player's render scale was outside DLSS's range for the
  mode in use: it renders at the nearest size DLSS allows). `aside_cap` / `aside_dlss`: `KILN_UI_GFX_ASIDE_*` bits (OptiScaler,
  Streamline, EnshroudedVKFG.ini, DLSSTweaks, nvngx.dll next to the game, NGX patched by another module, Kiln's overlay
  off). Then the cap, `rendered_fps`, `output_fps` (equal until a frame generation phase), `frame_ms`, the swapchain's
  size, and the DLSS in use: `dlss_quality` (`NVSDK_NGX_PerfQuality_Value`: 0 Performance, 1 Balanced, 2 Quality, 3
  Ultra Performance, 4 Ultra Quality, 5 DLAA; -1 none), `dlss_preset` (as asked: Kiln's, else the game's hint; 0 DLSS's
  default, 1..15 A..O; without bit 31, which the game's NGX SDK code sets on the default it fills in, so 0x8000000B
  reads 11, K), `render_width/height` (this frame's while the readout is on, else at creation) and
  `dlss_output_width/height`. Any thread.
- `graphics_set(&KilnUiGraphics)` (48 bytes): the player's settings. Kiln.Ui's call only, from kiln.json "graphics" and
  the Kiln menu's Graphics tab; a native plugin's is refused (`KILN_ERR_UNAVAILABLE`): mods never choose them.

## Chat completion's slot (internal, GitHub issue 44)

One slot after `graphics_status`: `chat_completion` at byte offset 1032 (the table was 1040 bytes in 0.2.0-dev.29.22). It is Kiln.Ui's
transport for Tab completion in the game's chat ([chat-history.md](../chat-history.md#tab-completion)), **not** an API:
a call from a native plugin is refused (`KILN_ERR_UNAVAILABLE`), and `KilnUiChatCompletion`'s layout (576 bytes) may
change with Kiln.Ui. It answers only while `ui.chatCompletion` is on (the default; `false` turns it off) and the sigdb
feature `chat.ui.completion` resolved. The game's UI thread owns every game read and the replace; the render thread
only gets copies:
- operation 1: a snapshot of the focused HUD or Social chat field (the line, the cursor, the field and a revision that
  changes with every edit, its rectangle in screen pixels) and one queued key action (3 Tab, 4 Shift+Tab, 5 Up, 6 Down,
  7 Escape);
- operation 2: whether there are suggestions and whether the list is active, which decides what the window procedure
  takes (Tab only with suggestions; Up, Down and Escape only with the list active). Turning them on names the
  snapshot's field and revision and is refused for a stale one;
- operation 3: one replace of the whole line, applied by the UI thread with `EditSetText` only when the field, the
  revision and the expected text still match.
Game evidence: [docs/re/chat-input.md](../../re/chat-input.md#tab-completion-cursor-node-and-rectangle).

After them: byte 1032 is `chat_completion` (Kiln.Ui's own chat Tab completion, refused from native plugins),
`subscribe_pointer_press` at 1040 (0.2.0-dev.30; check `header.size` and the pointer) and `chat_search` at 1048
(0.2.0-dev.30.3, Kiln.Ui's own Ctrl+R search, refused from native plugins: [below](#chat-searchs-slot-internal-github-issue-44);
the table is 1056 bytes). `subscribe_pointer_press`:
`fn(const KilnUiPointerPress*)` once per left or right press over the game window (its raw record and its window message
are paired), on the window thread, only while Kiln does not capture input and the cursor is not over a Kiln panel; an
answer of `KILN_UI_POINTER_TAKE` keeps the press from the game, its release always reaches it. A fault in `fn` removes the
subscription. The core's `kiln.ui.inventory` (slot clicks, `kiln_ui_inventory.h`) is its first user.

## Chat search's slot (internal, GitHub issue 44)

One slot after `subscribe_pointer_press`: `chat_search` at byte offset 1048 (0.2.0-dev.30.3; the table was 1056 bytes with
it, 1048 before; the pointer tail follows it). First built by bisdat (pull request 106). It is Kiln.Ui's transport for Ctrl+R reverse search of the chat
history ([chat-history.md](../chat-history.md#ctrlr-reverse-search-issue-44-phase-1c)), **not** an API: a call from a
native plugin is refused (`KILN_ERR_UNAVAILABLE`), and `KilnUiChatSearch`'s layout (10572 bytes) may change with
Kiln.Ui; a call whose `size` is smaller is refused (`KILN_ERR_INVALID_ARGUMENT`). It answers only while
`ui.chatHistory` is on and the chat field's rectangle is verified (the sigdb feature `chat.ui.completion` and the
rectangle constants resolved); otherwise `KILN_ERR_UNAVAILABLE` and Ctrl+R stays the game's. The game's UI thread owns
the search and the one replace of the editor; the window thread only queues keys and characters, and the render thread
only gets copies:
- operation 1: a snapshot. `active` (search is open), `suppressed` (Tab completion held off after an accepted line),
  the query (`query`, `query_len`, at most 256 bytes), the `field` and the editor `revision` the search started on,
  the field's rectangle (`x`, `y`, `width`, `height`), the number of matches (`total`), the selected match
  (`selected`), up to ten rows from match `first` (`count`, `lines`, NUL-padded UTF-8), and in `index` an opaque
  token for this list of matches: it changes with every query edit and every new search. Refused while the field or
  its rectangle no longer matches the search;
- operation 2: a clicked row. `index` is the absolute match, `field` and `revision` come from the snapshot and
  `selected` carries the snapshot's token. A stale field, revision or token is refused, and the click is checked again
  on the UI thread after that frame's queued keys, so it never picks a line from a list the player no longer sees. An
  accepted row goes into the editor with `EditSetText`, unsent, as Enter does;
- operation 3: Kiln.Ui's handshake that turns on Ctrl+R. Without it (an older Kiln.Ui.dll) Ctrl+R stays the game's.

No text is logged or kept: the search reads the history already in memory (which never holds the lines the
sensitive-command filter drops), the query lives only while the search is open, and the one log line (an accept refused
by the editor checks) names no text.

## The pointer and drag and drop (0.2.0-dev.30.3)

Three slots after `chat_search`, a size-guarded tail (GitHub issue 113): `pointer` at byte 1056, `drag_source` at 1064,
`drop_target` at 1072; the table is 1080 bytes (1056 before). Check `header.size >= offsetof(KilnUiApi, x) + sizeof(void*)`
and the pointer for each. They are Dear ImGui calls (`native/services/ui/draw_slots_pointer.inl`, shared with the UI
preview's shim), so like every drawing slot they work only inside a frame callback and answer false there otherwise.
Managed: `UiDraw.Pointer`, `ButtonHeld`, `DragSource`, `DropTarget` ([ui.md](../ui.md#the-pointer-held-buttons-and-drag-and-drop-since-020-dev303)).

- `bool pointer(KilnUiPointer* out)`: set `out->size` (24 bytes: `size, flags, x, y, held, reserved`) first. `flags`:
  `KILN_UI_POINTER_VALID` (x and y hold the pointer; ImGui has seen the cursor), `KILN_UI_POINTER_DRAGGING` (a
  `drag_source` payload of any kind is in flight). `held`: bit `1 << KILN_UI_MOUSE_*` per button held (1 left, 2 right,
  4 middle). False, nothing written: outside a frame, `out` NULL or smaller than 24 bytes.
- `bool drag_source(KilnStr kind, const void* payload, size_t len, KilnStr preview)`: after a widget
  (`BeginDragDropSource` with `SourceAllowNullID`, so text and images work too). True on the frames it is dragged; each
  such frame sets the payload (copied, at most `KILN_UI_DRAG_PAYLOAD_MAX` 4096 bytes; an empty one is allowed) and draws
  `preview` by the pointer (empty: no tooltip). A drag whose source stops being drawn while the button is held goes on
  with the last payload until the release (Dear ImGui draws "..." by the pointer); released over nothing, it ends and
  nothing is delivered.
- `bool drop_target(KilnStr kind, void* out, size_t capacity, size_t* len)`: after a widget (`BeginDragDropTarget`,
  `AcceptDragDropPayload` without `AcceptBeforeDelivery`). True once, on the frame a payload of `kind` is released over the
  widget: up to `capacity` bytes go into `out`, `*len` (when not NULL) is the whole length (`*len > capacity`: it was
  cut). The drop is not offered again, so pass `KILN_UI_DRAG_PAYLOAD_MAX` bytes and nothing is ever cut; `out` NULL
  with `capacity` 0 reports only the length.
- A kind is 1..32 bytes (`KILN_UI_DRAG_KIND_MAX`), no NUL, not starting with `_` (Dear ImGui's own); anything else is
  refused (false). Native kinds are shared by every caller (put your plugin's name in them, and never use
  `"k"` and 16 hex digits); Kiln.Ui passes `"k"` and the FNV-1a 64 of `"<mod id>\n<kind>"` in hex, so each managed mod's
  kinds are its own. The hash keeps mods from meeting by accident; it is no security boundary (any code can compute
  another mod's kind).
- Coordinates are the overlay's pixels, the space of every drawing slot. Kiln feeds Dear ImGui the game window's client
  pixels, the same pixels as long as the window's client area and the swapchain match (they always have, so far).

## The game's popup panels (0.2.0-dev.31.9, GitHub issue 117)

One slot after `drop_target`, a size-guarded tail: `popups` at byte 1080; the table is 1088 bytes (1080 before). Check
`header.size >= offsetof(KilnUiApi, popups) + sizeof(void*)` and the pointer.

- `KilnResult popups(KilnUiPopups* out)`: set `out->size` (88 bytes: `size, available, flags, count, frame, rect[4]`)
  first. The game's popup panels over its UI in the last completed UI frame: the item menu a right-click on an inventory
  slot opens, drop-downs, context menus (all built by one game function, docs/re/game-ui.md "Popup panels over the
  inventory"), and its error popup. `rect[0 .. count)` are the panels' rectangles `{x, y, width, height}` in the game's
  UI units, the space of `KilnUiVisibleSlot.outer_ui`: map them with `kiln.ui.inventory`'s mapping like a slot. `flags`:
  `KILN_UI_POPUP_PANEL` (a panel was drawn), `KILN_UI_POPUP_UNKNOWN` (a panel without a readable rectangle: its first
  frame, or more than `KILN_UI_POPUPS_MAX` 4 panels; treat everything as covered), `KILN_UI_POPUP_ERROR` (the error
  popup, modal over the whole canvas; feature `ui.error_popup`). `frame` is the UI frame (`KilnUiScreen.frame`).
  Any thread. `KILN_ERR_UNAVAILABLE` with `available` 0: feature `ui.popups` off (a pattern or constant that does not
  resolve on this build, the hook refused): nothing is known about popups. `INVALID_ARGUMENT`: NULL or too small.
- How: a raw link on the game's popup-panel builder (`ui/PopupWindowBegin`) reads, after the builder returns, its panel
  node's laid-out rectangle (`frame + ui/popup.panel`, then `node + ui/node.rect`), the previous frame's layout. No
  game function is called and nothing is written. Kiln.Ui leaves out the `Ui.Inventory` marks of the slots under a panel
  ([ui.md](../ui.md#the-games-inventory-slots-020-dev30)).

## A window's state for docking (0.2.0-dev.31.16, mod API 0.2.23, GitHub issue 149)

One slot after `popups`, a size-guarded tail: `window_state` at byte 1088; the table is 1096 bytes (1088 before). Check
`header.size >= offsetof(KilnUiApi, window_state) + sizeof(void*)` and the pointer. `KILN_UI_VERSION` stays 7.

- `bool window_state(KilnStr name, KilnUiWindowState* out)`: set `out->size` (24 bytes: `size, flags, x, y, width,
  height`) first. A window as Dear ImGui has it now, read only, in the overlay's pixels; frame callbacks only. `name` is
  the window's name as given to `begin` (ImGui's `"Title###id"` rules); empty: the window being drawn (its top-level
  window, also from inside a child region). `flags`: `KILN_UI_WINDOW_STATE_EXISTS` (ImGui knows the window: it was begun
  in this or an earlier frame; x..height are where it is now), `_MOVING` (the player holds it to drag it: ImGui's moving
  window, whichever part was clicked), `_RESIZING` (the player holds one of its resize grips or borders). Called before
  that window's `begin`, `x` and `y` are already this frame's when the player drags it (ImGui moves a dragged window in
  `NewFrame`); after it, where it was drawn. False: outside a frame callback, `out` NULL or too small (nothing written),
  or no window of that name (`flags` 0). It never makes a window.
- Kiln.Ui's window docking ([ui.md](../ui.md#docking-windows-since-020-dev3116-mod-api-0223)) asks it for every window of a dock
  group before drawing them, to know which one leads the frame, and `UiDraw.WindowMovedByUser` / `WindowResizedByUser`
  ask it for the current window. Tests: `native/services/ui/tests/window_state_test.cpp` plays ImGui frames headless with
  real drags and resizes.

## The overlay's drawing off: kiln.json and a session that turned it off (0.2.0-dev.29.18)

No table change. Player side: [ui.md](../ui.md#frame-generation-mods-and-drawing-off).
- **kiln.json `"ui": {"overlay": "off"}`** turns the overlay's drawing off from the game's start, the same as
  `KILN_UI_OVERLAY=0` (which wins where it is set, as `KILN_UI_OVERLAY=1` does). `"auto"` is the default. Read once by
  kiln.ui at start (`native/services/ui/overlay_setting.h`), top level only; the launcher's Settings writes it. For a
  native plugin it is the drawing-off mode as with `KILN_UI_OVERLAY=0`: `KilnUiStatus.error` says "the overlay's
  drawing is off: turned off in Kiln's settings (kiln.json ui.overlay)", and the drawing capability rows are unavailable
  with that text.
- **Drawing that turns off during a session.** The overlay's first failure (its submit, its resources, ImGui, a fault,
  or a present from a queue Kiln cannot draw on: [ui.md](../ui.md#when-the-overlay-fails-during-a-session)) turns its
  drawing off until the game closes. From then on frame callbacks and pre-overlay callbacks are no longer called,
  `KilnUiStatus.drawing` turns 0, `KilnUiStatus.error` reads "the overlay's drawing is off: <reason>", `wheel_open`
  returns `KILN_ERR_UNAVAILABLE`, and the rows `ui.windows`, `ui.draw`, `ui.menu_page`, `render.draw` and
  `kiln.screen` turn unavailable (`KILN_CAP_REASON_POLICY`, the same text) a moment later. Keys, native menu rows,
  `kiln.ui.tick` and the window's input subscriptions go on.
- **Pre-overlay callbacks** (`subscribe_pre_overlay`) are called only for a present on a queue the game got (not one a
  tool added to the device while it was made, such as frame generation's present queue) whose family has graphics;
  `queue_family` is that queue's own family, never a guess. A queue Kiln did not see the game get is looked up among the
  queues the game asked for when it made its device, so the game's own counts wherever it got it.

## kiln.render v2: outlines (0.2.0-dev.30.3)
`kiln.render` (registered by kiln-ui.dll next to kiln.ui) appends five functions after `image`: `outline`,
`outline_info`, `outline_remove`, `outline_clear`, `outline_status`; the version is 2. A reader checks
`header.size >= offsetof(KilnRenderApi, outline_status) + sizeof(void*)`. They outline an entity's model with the
game's highlight groups Group1 and Group2, which Kiln colours itself. Managed: `Ui.World.Outline`, `WorldOutline`,
`OutlineOptions` (Kiln.Ui). Capability `ui.world_outline`. Usage and the C table: [../render.md](../render.md#outlines-since-020-dev303);
RE: [../../re/client-render.md](../../re/client-render.md#highlight-groups-outlines).

## kiln.ui.ammo: the HUD's ammunition and spell lists (0.2.0-dev.30.6)

`kiln.ui.ammo` is a **new v1 table** (`native/core/abi/services/kiln_ui_ammo.h`, 32 bytes), served by kiln-tools on its
existing checked list-builder chain, client only. Managed: `Ui.AmmoList` (Kiln.Ui), page
[../ui.md](../ui.md#the-huds-ammunition-and-spell-lists-020-dev306). Capabilities `ui.ammo.list` and `ui.ammo.pick`
([caps.md](caps.md)). The game's hooks read only owned immutable snapshots; nothing calls into a mod from a hook.

| Member | Thread | What it does |
|---|---|---|
| `publish(owner, KilnUiAmmoPublish*)` | client main / UI | Copies up to 64 `KilnUiAmmoRow` rows (`item`, shown `count`, `flags` 0, `row`: the publisher's key, echoed in picks) for one kind (`KUA_KIND_ARROW` 1, `KUA_KIND_SPELL` 2) with `KUA_MODE_APPEND` 1 or `KUA_MODE_REPLACE` 2; `KUA_MODE_GAME` 0 clears. `serial` and `session` must be the context's. `KILN_ERR_INVALID_ARGUMENT`: a malformed, duplicate or unknown item (not of that kind in the client's catalog), more than 64 rows, a stale serial or session; `KUA_ERR_BUSY` (200): another owner holds the kind; `KILN_ERR_NOT_FOUND`: no context yet; `KILN_ERR_UNAVAILABLE`: the row is off. A `REPLACE` snapshot is also drawn over the game's newer builds of its list in the same client session, generation, local player and held item, as if made for them, until the owner publishes or clears that kind again, retires, or the world stops, at most 1 s from the first such build; then the game's own rows show until the next publication, and `kiln.log` says so once at info level (`ui.ammo: the spell list shows the game's own rows: ...`). `APPEND` snapshots apply to their own serial only (since 0.2.0-dev.31.21, GitHub issue 151). |
| `context(owner, kind, KilnUiAmmoContext*)` | any | An owned copy of what the list is built for: `session`, `local_player`, `held_item`, the game's own rows (`game_count`, `game_rows[64]`) and `game_serial`, which changes with the held item, the game's rows, sources or counts, the actor or the session. `reason` is a diagnostic: `KUA_REASON_NO_SESSION` 1, `NO_CONTEXT` 2 and `STALE` 4 come with `KILN_ERR_NOT_FOUND` and `ok` 0 (also before the game first built a list); `CATALOG` 6 (the client's item catalog not read yet) with `KILN_OK` and `ok` 0; `LIMIT` 3 with `ok` 1: a usable context of which only the game's first 64 rows fit; `FAULT` 5. The game's rows are the last observed ones, not proof that a stack still exists at a pick (the pick checks the backpack again). |
| `take_picks(owner, KilnUiAmmoPick*, capacity, written*)` | client main / UI | Drains the owner's queue (at most 64 a call; the oldest is dropped on overflow) of selections of the owner's rows that the game did not list itself (in either mode) and whose item has no stack in the backpack: `kind`, `item`, `count`, `row`, `session`, `step`. Since 0.2.0-dev.31.10 (GitHub issue 145) also a row the game listed with stock whose stack the backpack held when the rows were bound and no longer holds at the pick, and, while the game's new rows show before the owner publishes for them (`APPEND`, or `REPLACE` past its 1 s), a provided item's count-0 game row whose item has no stack; a `REPLACE` snapshot drawn over a newer build is bound to that build's rows, so its picks are decided as for any bound row. Kiln keeps the game's equip request for those rows from running and moves no item; since 0.2.0-dev.31.12 (GitHub issue 140) a pick the player made (the game's cycle or direct selection, not its automatic selection) of the list the HUD shows also shows that list again for its usual time, as the host's answer to a game row's pick does; a pick of a provided row that goes to the game is logged at debug level (`ui.ammo: a pick of ... went to the game: ...`, at most 8 lines a second). The caller drops an earlier session's picks. |

Kiln.Ui calls all three under one owner of its own: it composes every mod's providers into one snapshot per kind, builds it
again when `game_serial` or the session changes, and drains the picks at the start of each UI frame (`kiln.ui`'s
`subscribe_ui_tick`), before it publishes again: a pick carries no publication of its own, so its `row` key (the row's
index in the publication) is read against the publication that was up when it was queued, and a key that does not name
that item there is dropped. An owner's retirement or a world's stop clears its snapshots and queued picks.

## Captured item detail (Since 0.2.0-dev.30.5)

`kiln.ui.item_detail` is a **new v1 table**, not an append to `kiln.ui` or `kiln.ui.values`.
Its only function, `copy(owner, query, snapshot, lines, capacity)`, is callable on any thread;
see `native/core/abi/services/kiln_ui_item_detail.h`. Check header size/version and function
pointer. Records are size-tagged: query 20 bytes, snapshot 56 bytes, line 4112 bytes (4096
length-delimited UTF-8 bytes plus size, flags, value and byte count). The x64 table is 16 bytes,
with `copy` at offset 8. No existing table layout changes.

This is **passive capture**, not on-demand item computation or a new drawing API. First use
installs capture. The client's main/UI thread copies text constructed inside the current
ItemDetail call; Kiln-injected tooltip lines are excluded. Any-thread readers copy completed
immutable records under a mutex, not live item/world memory. Stat numbers are captured after
tooltip value providers, with numeric metadata on the first constructed text row. Text already
contains the game's formatted numbers; do not append the metadata again. A number-only draw
gets empty text and a float. No opaque label is reinterpreted as a string.

A query's `entity` is a CLIENT presentation id, never an arbitrary server inventory entity.
Entity queries match only that instance. `entity=0` queries `item` plus optional level/rarity
(`UINT32_MAX` means unspecified), choosing the latest matching capture. This may include the
inspected instance's gems/perks, not pristine item-type defaults. No typed semantic stat,
socket, perk-unlocked or food-effect model is supplied; colour/icon-only information is absent.

The last 64 captures/refusals are retained in capture order; reads do not promote entries.
A completed panel replaces the same entity, or item+level+rarity for entity=0, even on rejection.
Session/world changes, missing client state or no local player clear captures on next read or
capture. Language and item-state changes require reinspection; they are not automatically
observed. A snapshot already copied by a caller remains historical data. Limits: 64 lines,
4096 UTF-8 bytes per line, 32768 total bytes; reject invalid/oversized/empty panels whole.

`KILN_OK` with `ok=0` is a miss: `snapshot.reserved` carries 0 (no matching capture), 1 (offline,
missing client state or no active local-player session), or 2 (rejected panel: empty, invalid
text/number, read failure or budget). A short destination gets `BUFFER_TOO_SMALL` and required
`count`, with no partial rows. `UNAVAILABLE` means wrong side, missing symbols/layout, disabled
capability or hook failure; `REVOKED` means retired owner; invalid/short records, owner zero,
empty query or null rows with nonzero capacity get `INVALID_ARGUMENT`.

Capability `ui.item_detail` is client-only (`wrong-side` on a server); missing build symbols/
constants make it unavailable, missing table lowers it to `service-older`, and hook failure to
`faulted`. Offline/no-session and per-item misses are result reasons, not permanent capability
failures. Client check: inspect a real item panel and compare the text and numbers read by a
mod on Proton and native Windows. **Real-client hook coverage remains unverified.**

Managed API, cache/fallback contract and sample: [the UI guide](../ui.md#captured-item-detail-since-020-dev305).

## Negotiated slot subscriptions (dev.30.5 review correction)

The released `KilnUiVisibleSlot` and `kiln.ui.values.subscribe_slots` callback stay frozen at
112 bytes per row. `KilnUiVisibleSlotV2` is 120 bytes, adding level and rarity at 112/116.
A new **`kiln.ui.slots` v1** table has `subscribe(owner, record_size, fn, user, out)`; it accepts
only `record_size == 120` and delivers typed V2 rows. An unknown negotiation is refused.
Unsubscribe through the existing kiln.ui.values handle registry. Capability: `ui.slots.inspect`.
No existing table grows. Size-aware `copy_slots` still supports both strides through the first
output row's size. `Ui.Values.SubscribeSlots` prefers the negotiated table and expands frozen
112-byte callbacks when running on an older core. TestKit models both callback layouts.

### Native slot backgrounds (since 0.2.17)
| Capability | API | Side / thread | Native table |
|---|---|---|---|
| `ui.slots.tint` | `Ui.Inventory.TintWhen` | Client; register/dispose any thread, rule game UI thread | `kiln.ui.tints` v1 (24 B) |
Native background colours only; no overlay, gameplay mutation or network message. Rules receive owned item
view values, never game addresses. See [UI](../ui.md#native-inventory-background-colours-since-0217).

### Native storage-chest row (since 0.2.17)
| Capability | API | Side / thread | Native table |
|---|---|---|---|
| `ui.chest.buttons` | `Ui.Inventory.AddChestButton` | Client; register/dispose any thread, activation game UI thread after the chest builder | `kiln.ui.chest` v1 (24 B) |
A native entry inside the game's action row; four total, unique captions, current container identity. No
vanilla action id is reused, no gameplay write, and no overlay is required. Validate client requests on the server.
