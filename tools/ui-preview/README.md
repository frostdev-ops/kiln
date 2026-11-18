# UI preview: the Kiln menu as PNGs, without the game

Renders the mod manager (`managed/services/Kiln.Ui/ModManager.cs`) on Linux with no game, GPU or Wine, so its layout can
be judged and iterated.

```sh
tools/ui-preview/preview.sh                                  # every scene at 1920x1080, 2560x1440 and 1920x1080 at UI scale 1.5;
                                                             # the game-page scenes also at 3440x1440 and 1920x1200
tools/ui-preview/preview.sh --scene problems --size 2560x1440 --scale 1.25
tools/ui-preview/preview.sh --font ~/fonts/Some.ttf          # another TTF for the game look
tools/ui-preview/preview.sh --check-z                        # a window opened over the page (SET's tools) stays in front
                                                             # when the page is clicked, and the menu opened over a mod's
                                                             # full-screen screen is in front of it; exit 1 otherwise
tools/ui-preview/preview.sh --check-text                     # types 3 KB into a ShroudForge text and text-area setting;
                                                             # exit 1 when ImGui is given a buffer size its own copy of the
                                                             # active text does not have (it would write past it)
```
Output: `$KILN_OUT/preview/<scene>-<size>[@scale].png` (and a text summary of the mod list for some scenes);
`KILN_OUT` defaults to `<repo>/.out`. Needs g++, python3 and the .NET SDK.

How it works:
- `shim.cpp` is kiln.ui without the game: a shared library that hands Kiln.Ui the same `KilnUiApi` table the game does.
  The drawing slots are the game's own (`native/services/ui/draw_slots.h`), and so are the looks and the font loading
  (`style.cpp`). A headless Dear ImGui context runs the frames; a small software rasteriser turns its draw lists into
  pixels over a painted stand-in for the game's picture.
- `Kiln.Ui.Preview` (C#) loads the shim, points Kiln.Ui at it and runs Kiln.Ui's real frame code with the Kiln menu
  open. Each scene writes sample state files and a profile into `$KILN_OUT/preview/work/<scene>/kiln/`, and the real
  `ModCatalog` reads them, so the preview also exercises the reader.
- The game's UI font: `game_font.py` picks it as `style.cpp` does (keen::UiFontResource, the default language group's
  regular font, and its bold face for the game page's title) and reads them from the local game with `tools/kfc` into
`$KILN_OUT/ui-preview/game-ui-font.ttf` and `game-ui-font-bold.ttf`. It is
  never copied into the tree. Without the game the preview uses a system TTF (DejaVu Sans / Noto Sans) or ImGui's own font.

- The game's UI colours: `game_colors.py` reads the local game's `keen::FbUiBundle` and the reflection layout of the
  colour fields kiln.ui reads (with `tools/kfc`) into `$KILN_OUT/ui-preview/game-ui-bundle.bin` and `.layout.txt`; the shim
  fakes `kiln.types` from that layout, so `style.cpp`'s real reader runs. Never copied into the tree. Without the game (or
  with `--no-game-font`) the game look is its neutral stand-in, as on a build where the read fails.

- Kiln's texts in another language: `--language zh-CN --locales DIR` (a folder with `zh-CN.json`, e.g. a stub translation
  kept outside the tree) sets kiln.json's `ui.language` for every scene. For a Chinese, Japanese or Korean language
  `game_fonts.py` reads the game's own `keen::UiFontResource` and the font files of that language's group (and the default
  group's) from the local game with `tools/kfc` into `$KILN_OUT/ui-preview/game-fonts/` (never copied into the tree), and the
  shim serves them to style.cpp's real readers (`kiln_preview_font_resource`), so the preview loads them as the game would:
  Kiln's texts stay English until the group's font joined the atlas. Each scene prints when the glyphs were ready, the
  first frame's time in the language and the font atlas (`kiln_preview_atlas`: texture size, glyphs made, font sources);
  the PNG is `<scene>-<language>-<size>.png`.

Scenes (`Kiln.Ui.Preview/Program.cs`, `Scenes`): `typical` (25 mods over Kiln, EMF, EML, SET and ShroudForge, one failed,
one switched off), `typical-emf` (one engine), `typical-search`, `typical-config` (a mod's settings inline),
`typical-keys` (three Down presses: the selection follows the focus), `problems` (failed mods in several engines, a
missing folder, ShroudForge not installed, Ember, a stale and a cut-off report), `problems-ember`, `empty`, `network`,
`keys`, `overlay`, the looks of Kiln's UI (0.2.0-dev.29.16: `look-settings` (Kiln settings > Look), `look-translucent`,
`look-translucent-settings`, `look-kiln` (ember), `look-theme` (a sample mod's theme the player picked), `look-scale` (size 125 %,
opacity 85 %), `look-game-page-translucent`, and `look-windows-game` / `-translucent` / `-kiln` / `-theme`: the Kiln menu
closed, a sample mod's window in its own theme beside a Kiln window, a HUD line and a toast), and `game-page` / `game-page-bright` / `game-page-problems`: the page inside the game's main menu
(`GamePageHost`), with the rectangle and scale the game would publish at that size, over a stand-in for the close-up
shot and its vignette (dark, or bright and busy to test readability). Add one by writing its files in a new method and listing it in `Scenes.All`.
`--scene checks` (only when named) draws the guided check runner's screen and HUD line (`managed/Kiln.Testing/CheckUi.cs`)
from the sample states in `CheckSamples.cs`: `checks-list` (a check running), `checks-confirm` (a Confirm text waiting for
its yes), `checks-note` (the fail note being typed, with a guard warning).

Limits: the backdrop is not the game's picture; ImGui is rasterised on the CPU (close to the GPU result, not equal); the
game's own menu entries and transitions are not drawn; the gamepad is not simulated (keys are: `Scene.Keys`).
