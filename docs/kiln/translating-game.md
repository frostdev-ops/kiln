# Translating Kiln: in game

Kiln's own texts in the game (its menus, windows and messages) come from one file per language, in the format of a mod's
`locales/*.json` ([modding/lifecycle.md](modding/lifecycle.md)). English is the source; a text a translation does not have
yet shows in English, so a translation can be partial and grow. The Kiln Launcher's texts are a separate set of files with
the same format (`docs/kiln/translating.md`).

## What is translated

Everything Kiln itself shows a player in the game, since 0.2.0-dev.29.16:
- the Kiln menu (stand-alone and inside the game's main menu): its title, sections, key hints, the **Mods** page (engine
  rows, the mod list, chips, search, empty states, the details column and every reason or help sentence about a mod), the
  generated settings pages' frame, ShroudForge's settings page, shroudlink's menus, SET's tools entry, **Network**,
  **Keys**, **Overlay**, and **Kiln settings** (Look, Language);
- the creative menu, the guided check runner, the talent tree's frame, block windows' frame, the Item details window, the
  developer windows (Systems, Types, Bindings, Resources, Hooks, Game Log, Status, Console), the old small Kiln window;
- Kiln's toasts and HUD lines (a server's refusal, a fault, an ability's cooldown, Gem Forge, creative mode), the entries Kiln
  adds to the game's pause menu (Creative, Item details) and the descriptions of Kiln's own keys on the Keys page;
- what kiln-ui.dll draws itself: the **Mods** entry in the game's main and pause menus, Kiln's line in the main menu's footer
  ("Kiln 0.2.0 - 12 Mods Loaded") and the window shown when the managed host is not running.

**Stays as it is**, on purpose:
- **Mods' texts**: a mod's names, settings, windows, toasts, status lines and menu entries are its own (a mod translates
  them with its own `locales/`; [modding/lifecycle.md](modding/lifecycle.md)). Mods follow the game's language, not Kiln's
  setting.
- **What a server sends**: a refusal's reason, the creative menu's answers from the server, chat command replies, talent
  messages from the server. A dedicated server always speaks English.
- **Key names** (`F8`, `Insert`, `Ctrl+K`, `Mouse4`, `WheelUp`): they are what `keybinds.json` stores, what mods and docs
  name, and what the Keys page shows (`native/services/ui/key_names.h`). The words around them are translated ("Press
  {key} to close.").
- **Product and engine names** on their own (Kiln, EMF, EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick,
  shroudlink, Ember), Kiln's version, mod ids, file names and paths standing alone, command names (`/kiln`, `/busyhands`).
- **State words read from files** (`waiting`, `applied`, `write-failed`, `off-in-shroudforge`...): they are compared and
  logged; the chips and sentences that explain them are translated.
- **kiln.log** lines, exception messages (ShroudForge's settings editor shows its save errors as they are thrown, in
  English), the developer windows' data (types, systems, resources, the game's log) and the console's output.

## The files

| | In the repository | In a game folder |
|---|---|---|
| English (the source) | `runtime/locales/en.json` | `<game>/kiln/locales/en.json` (also built into Kiln.Api: a missing or broken file never shows keys) |
| A translation | `runtime/locales/<language>.json`, e.g. `zh-CN.json` | `<game>/kiln/locales/<language>.json` |

Every Kiln update replaces the folder (the runtime bundle's core component, `tools/package/stage.sh`). Name a file by its
language tag: `zh-CN.json`, `zh-TW.json`, `ja.json`, `ko.json`, `de.json`, `pt-BR.json` (case and `_` do not matter).

The format, in short (the launcher's translating guide says the same at length):
- One JSON object; a nested object makes dotted keys (`"menu": { "title": "Mod Manager" }` is `menu.title`). Keep the
  nesting of `en.json`: copy it, then replace the English. UTF-8.
- `language.name`: your language in your language (`"简体中文"`); the Language setting lists it.
- Placeholders `{0}`, `{name}`, `{count:N0}` stay exactly as written; move them where your grammar needs them; never add
  or drop one. `{{` and `}}` are literal braces.
- Plurals are `.one` / `.other` pairs (`"loaded": { "one": "...", "other": "..." }`). Chinese, Japanese and Korean have one
  form: write only `.other`. Russian, Ukrainian and Belarusian (`one`, `few`, `many`, `other`) and Polish (the same
  four) get their `few` and `many` in game since 0.2.0-dev.29.19; a form a file lacks falls back to its `.other`.
- Groups follow Kiln's pages: `menu` (the Kiln menu's frame), `mods`, `catalog` (the Mods page's sentences about mods),
  `config`, `settings`, `set`, `shroudforge`, `shroudlink`, `keys`, `network`, `overlay`, `classic` (the old small window),
  `details` (Item details), `gamemenu`, `toast`, `refusal`, `creative`, `checks`, `talents`, `blocks`, `abilities`,
  `gemforge`, `dev` (developer windows), `native` (what kiln-ui.dll draws: the Mods entry, the footer, the fallback window).
- Short labels (tabs, buttons, chips, table columns) have little room; a long translation is cut or wraps.
- Captions written in capitals in English (`ALL MODS`, `LOOK`) are capitals on purpose; a script without case writes them
  as usual.

## Which language Kiln shows

kiln.json's `ui` section, key `language`:
```jsonc
"ui": {
  "language": "auto"   // auto (the default): the game's text language; or a tag: "zh-CN", "de", "en"
}
```
- **auto** follows the game's text language as the game reports it (`Game.Language`, `de-de`, `zh-cn`; English before
  the game's UI has drawn a frame). A player who switches the game's language sees Kiln follow at the next frame.
- **A tag** forces that language whatever the game shows (a Chinese player in an English game, or the other way round).
- In game: **Mods > Kiln settings > Language**, under Look: *Automatic (the game's language: ...)*, English, and every file in
  `kiln/locales`, each in its own name. A change shows at once and is saved with the look. The launcher's Settings set the
  same key.
- Lookup: the file of the exact tag (`zh-cn`), then its main language (`zh`), then English; `es-mx` tries `es-419`, `nb`
  and `no` try each other (`ModLocale.LookupOrder`). Kiln's texts stay in that language while mods' texts follow the game.

## Cyrillic and the other scripts of the game's default font group

The overlay's game-style font is the game's Harmonique Display, which has Latin only. Since 0.2.0-dev.29.19 kiln-ui.dll
also reads **the other fonts of the game's default language group** at run time, for every language, and merges them into
the overlay's fonts (its default, the game's UI font and bold) after their Latin source, as the game routes them: Noto
Serif for Cyrillic (U+0400 to U+04FF, and U+20A9 and U+2116), Noto Sans Thai for Thai (U+0E00 to U+0E7F). So Russian,
Ukrainian and Bulgarian, Kiln's texts or a mod's, draw in game with the game's own Cyrillic font; Latin keeps Harmonique
Display. Kiln ships no font.
- **Which files.** The default group's regular glyph sets after its first one (the Latin source), each limited to its own
  rune ranges (`native/services/ui/font_group.h` `pick_default`). Only files up to 2 MiB, at most 4 and 4 MiB together
  (`font_budget.h` `admit_default`): on client 26430625 Noto Serif (515 KB) and Noto Sans Thai (62 KB). The group's CJK
  fallbacks (Noto Serif SC 11.2 MB, KR 7.4 MB) are left out: a Chinese, Japanese or Korean language's own group brings them
  (below), after these.
- **As the game, nearly.** The game draws Russian, Bulgarian and Ukrainian with a group of their own, Noto Serif for Latin
  too; Kiln keeps one Latin look for every language (as it does for CJK) and takes only the Cyrillic from Noto Serif. Bold
  text gets the regular Noto Serif, as CJK text gets its regular face.
- **Cost.** 577 KB (0.5 MiB) read once at the first overlay frame, on a worker (1-2 ms from a warm cache in the preview),
  and kept for the process; the atlas makes a Cyrillic glyph only when one is drawn (ImGui 1.92 dynamic atlas), so a Latin
  language costs no atlas space more. Measured in the preview with the real fonts: the Mods page in Russian uses a 512x512
  atlas (1 MiB RGBA, about 580 glyphs), in English the same 512x256 atlas (512 KiB) as before; the first frame in Russian
  about 65 ms of software rendering in the preview.
- **No "????".** A Cyrillic language (ru, uk, bg; be, mk, sr but sr-Latn, kk, ky, mn and tg count as ru-RU for the glyphs)
  waits for those fonts as a CJK language waits for its group: until they joined the atlas, Kiln's overlay texts are
  English (the game-drawn `native.menu.` / `native.footer.` texts are in the language at once). When they cannot be read
  (no `kiln.assets`, another build's data) or have no Cyrillic, kiln.log says why and Kiln's texts stay English:
  `ui: the game's default font group is not available (...): Kiln's texts in a Cyrillic language stay English, ...` or
  `ui: the game's default font group has no Cyrillic font Kiln can use: ...`. `KilnUiLookInfo.glyphs` reports it for the
  language's `keen::LanguageId` (9 ru-RU, 24 bg-BG, 34 uk-UA). The Language entry shows Русский once those fonts are in
  (in a Latin game, a Cyrillic language's name is its tag, as CJK's: the readout is about the language in use).
- kiln.log: `ui: the game's default font group joined the overlay's font atlas (group 0, fonts 10, 6 of 28, 0.5 MB;
  Cyrillic; fonts 13, 12 left out (over 2 MiB: a Chinese, Japanese or Korean language's own group brings them); layout
  from the game's reflection; read in 1 ms)`.

## Chinese, Japanese and Korean

The overlay's own fonts are Latin (the game's Harmonique Display, the system font). When Kiln's language, or else the
game's, is Chinese, Japanese or Korean, kiln-ui.dll reads **the game's own font of that language group** at run time, as it
reads the game's UI font, and merges it into the overlay's fonts after their Latin source. Kiln ships no font.
- **Which font.** The game's `keen::UiFontResource` has 6 language groups; zh-CN, zh-TW, ja-JP and ko-KR each have their
  own. Kiln takes that group's regular glyph sets in the group's order, each limited to the game's own rune ranges, so the
  overlay gets the glyphs the game would draw for that language: zh-CN Noto Serif SC (runes up to U+ABFF) and Noto Serif KR
  (U+AC00 to U+FFFF), ja-JP Noto Serif JP Light and KR, zh-TW Noto Serif TC and KR, ko-KR Noto Serif KR
  (`native/services/ui/font_group.h`; the layout from the game's reflection, checked against client 26430625's data).
  Latin keeps the overlay's font; only what it lacks comes from the group's font, after the default group's Cyrillic and
  Thai fonts (above).
- **Cost.** The overlay's ImGui font atlas makes glyphs as they are drawn (ImGui 1.92+, dynamic textures), so a group's
  font costs nothing until text in it is drawn, and its ranges only route runes to it. ImGui's
  `GetGlyphRangesChineseSimplifiedCommon` (2,500 ideographs) is a preload list for renderers without dynamic textures: it
  would save no atlas space here and would show boxes for any rarer character a translation or a mod uses, so Kiln uses
  the game's own ranges. Measured in the preview with the real fonts: the Mods page in Chinese uses a 512x256 atlas
  (512 KiB) with about 320 glyphs, the same atlas as in English; reading the group's two files (17.7 MB) took 50-70 ms from
  a warm cache (a cold hard disk takes longer); the first frame in Chinese about 60-100 ms of software rendering in the
  preview. The files stay in memory while the language is in use (`font_budget.h`: one file at most 16 MiB, a group at
  most 24 MiB; zh-CN is 18.6 MB on 26430625). Another language's group replaces them; a Latin language drops them.
- **Mods' own texts (0.2.0-dev.31.24).** A mod's text in Chinese, Japanese or Korean drawn while neither Kiln's language
  nor the game's is one (HTR, 2026-11-27: the settings of Mineral Production Boxes, Nexus 347, are Chinese, and Kiln's
  ShroudForge page drew them as `????`): kiln-ui.dll looks at the text it is given to draw (every label, text, combo item
  and text field value of any `Ui` call, and the render service's text: `native/services/ui/text_glyphs.h`), and at the
  first such rune loads the zh-CN group (Noto Serif SC for Chinese and kana, KR for Hangul syllables) for the rest of the
  process; a Latin language keeps it then. The check costs one atomic read per text once the group is asked for, and a
  byte scan of each text before that (Latin text never matches). When the game's font cannot be read, kiln.log says so once
  (below) and the text stays `?`. The text shows `?` for
  the frames until the group has joined (well under a second from a warm cache). Cost: nothing downloaded; the group's
  18.6 MB of font files stay in memory from then on, and glyphs are made as they are drawn. Japanese text drawn this way
  gets the SC font's shapes of kana and kanji. Kiln's language, and the game's, still pick their own group first; look_info's
  `glyphs` stays about them. kiln.log: `ui: a text in Chinese, Japanese or Korean is drawn (a mod's) ...`.
- **No boxes.** Until the font has joined the atlas, Kiln's overlay texts are English (the game's own menu entries and the
  footer, which the game draws with its own fonts, are already in the language). When the font cannot be read (no
  `kiln.assets`, another build's data), kiln.log says why (`ui: the game's font of language 20 is not available (...)`),
  the overlay keeps its Latin fonts and Kiln's texts stay English. `KilnUiLookInfo.glyphs` says which
  (`KILN_UI_GLYPHS_LATIN / LOADING / READY / FAILED`).
  The same holds for what kiln-ui.dll draws in the overlay itself (the graphics readout, the fallback window): only the
  `native.` texts the game draws (`KilnText.GameDrawnPrefixes`: `native.menu.`, `native.footer.`) are in the language
  meanwhile. In **Language**, a Chinese, Japanese or Korean language's own name (简体中文) shows once the overlay has its
  glyphs, else its tag: an English game lists Simplified Chinese as `zh-CN` (since 0.2.0-dev.29.18).
- kiln.log: `ui: the game's font of language 20 joined the overlay's font atlas (group 1, fonts 13, 12 of 28, 17.7 MB; ...)`.

## Testing a translation

1. Put your file into `<game>/kiln/locales/` (next to `en.json`).
2. In game, open the Kiln menu (Insert), **Mods > Kiln settings > Language**, and pick your language. Switching away and
   back reads the files again, so an edit shows without restarting the game.
3. A key your file lacks shows in English; a key your file has that English has not is ignored; a broken file is skipped
   (kiln.log: `locale file zh-CN.json of mod kiln cannot be read: ...`) and English shows.
4. Without the game: `tools/ui-preview/preview.sh --language zh-CN --locales <folder with your file>` renders the Kiln menu
   to PNGs (with the game's CJK font when the game is installed).
5. The check `KilnTextKeysTests` (Kiln.Ui.Tests) reports keys your file has that English has not and placeholders that
   differ from English.

## For Kiln's developers

- **One table for both sides.** `KilnText` (`managed/Kiln.Api/KilnText.cs`, internal, visible to Kiln's services) loads
  `<kiln>/locales` with `ModLocale`'s reader over the English built into Kiln.Api, and answers in the language in use:
  `KilnText.T("mods.search.hint")`, `KilnText.T("mods.empty.no_match", ("search", text))` (named), `KilnText.T(key, a, b)`
  (positional), `KilnText.Plural("native.footer.loaded", n, ("version", v))` (`{count}` filled). Lookups take no lock.
- **Native texts.** kiln-ui.dll draws a few texts itself (`native/services/ui/ui_text.h`): Kiln.Ui hands it every key under
  `native.` in the language in use, and the language, with `set_texts` (`kiln_ui.h`, [services/ui.md](services/ui.md)) at
  start and on every change. Until then, and without the managed host, kiln-ui.dll draws its built-in English, which
  `native/services/ui/tests/ui_text_test.cpp` checks against en.json's `native` section key by key. Plural forms reach it
  resolved (`.one` is `.other` for a language without a singular).
- **Live.** `KilnText.Changed` fires and `KilnText.Generation` changes when the language does. Text a page keeps (rows built
  on a worker, combo item arrays, window titles, key descriptions, menu entries Kiln added) is rebuilt by comparing the
  generation or on the event: `UiWindow.Retitle`, `KeyBinding.Description` (internal set), `MenuEntry.Text`. Never use
  `UiText` for Kiln's own texts: it follows the game's language, not Kiln's setting.
- **Rules.** Keys are lower case and dotted, grouped by page. English stays exactly as it was. A sentence is one text with
  placeholders, never pieces glued in code. An ImGui id stays outside the text: `KilnText.T("dev.filter") + "##filter"`.
  A key built at run time starts with a literal prefix that ends in a dot (`KilnText.T("catalog.about." + kind)`).
- **The check** (`managed/tests/Kiln.Ui.Tests/KilnTextKeysTests.cs`): every literal key of a `KilnText.T` / `KilnText.Plural`
  call in `managed/services`, `managed/Kiln.Testing` and `managed/Kiln.Api`, and every `"native.…"` key in
  `native/services/ui`, is in en.json; a call fills exactly the placeholders its English text has; every en.json key is used;
  no key twice; the built-in English is the file; a translation's keys and placeholders are English's.
