# kiln.ui window size constraints

See [the overlay and managed UI guide](../ui.md) for the other window calls.

`KilnUiApi.set_next_window_size_constraints(min_w, min_h, max_w, max_h)` is appended
at the end of the native table, after `subscribe_key_ex`; the service version stays 7.
It forwards to Dear ImGui `SetNextWindowSizeConstraints` for the next `begin`, in
screen pixels. Call it inside a frame callback before `begin`, every frame that
needs constraints. Like the neighbouring window setters it does nothing outside
a drawing frame, including `KILN_UI_OVERLAY=0`.

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
  (play data `+play.target`, 0x2c50 on client 26430625: the station's entity id), else 0; also 0 when the constant
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
- `KilnUiLookInfo.glyphs` (byte 30; it was reserved) is `KILN_UI_GLYPHS_LATIN` (0, no such language), `LOADING` (1),
  `READY` (2) or `FAILED` (3, kiln.log says why), and `glyph_language` (byte 31) the `keen::LanguageId` it is about (20 zh-CN,
  21 zh-TW, 11 ja-JP, 13 ko-KR). Kiln.Ui shows its overlay texts in English until `READY` for its language.
- Host tests (`tests/run.sh`): `ui_text_test.cpp` (the handoff, plurals, placeholders, the built-in English against
  en.json), `font_group_test.cpp` (the group, sets and exclusions from made-up resource bytes, another layout, bad data, the
  budget), `group_host_test.cpp` (style.cpp's loading on a headless ImGui context with a made-up resource and a system TTF:
  the language's choice, the state, merging into fonts that arrive later, replacing, removing, a failed read, the layout
  from reflection; `KILN_TEST_CJK_FONT=<an extracted CJK font>` also checks a real CJK glyph).

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
  default), `render_width/height` (this frame's while the readout is on, else at creation) and
  `dlss_output_width/height`. Any thread.
- `graphics_set(&KilnUiGraphics)` (48 bytes): the player's settings. Kiln.Ui's call only, from kiln.json "graphics" and
  the Kiln menu's Graphics tab; a native plugin's is refused (`KILN_ERR_UNAVAILABLE`): mods never choose them.

