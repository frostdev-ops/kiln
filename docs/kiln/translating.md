# Translating Kiln

Kiln has two sets of texts a player reads: the **Kiln Launcher** (the desktop app) and **Kiln's own texts in the game**
(its menus, windows and messages). Both use one file per language in the same format, the format of a mod's
`locales/*.json` ([modding/lifecycle.md](modding/lifecycle.md#mod-texts-locales)). English is the source; a text a
translation does not have yet shows in English, so a translation can be partial and grow.

## Where the files live

| What | English (the source) | Your language |
|---|---|---|
| Launcher | `launcher/src/i18n/en.json` | `launcher/src/i18n/<language>.json`, e.g. `zh-CN.json` |
| In game ([translating-game.md](translating-game.md)) | `runtime/locales/en.json` | `runtime/locales/<language>.json`; in a game folder `<game>/kiln/locales/<language>.json` |

Name the file by its language tag: `zh-CN.json` (Simplified Chinese), `zh-TW.json`, `de.json`, `pt-BR.json`, `es-419.json`.
A main language (`de.json`) serves all its regions; a regional file (`pt-BR.json`) wins over it for that region.

The save editor inside the launcher (Saves, Edit) has its own texts and is not covered here.

## The format

One JSON object. Values are text in quotes. A nested object makes dotted keys, so these two say the same:

```json
{ "settings": { "title": "Settings" } }
```
```json
{ "settings.title": "Settings" }
```

Translate the values, never the keys. Keep the nesting as `en.json` has it (copy `en.json`, then replace the English).
The file is UTF-8. Comments (`//`) and a trailing comma are accepted, but plain JSON is safest.

`language.name` is your language's name in your language (`"简体中文"`): the language menus show it.

## Placeholders

The program fills these in. Keep each one exactly as written, move it wherever your grammar needs it:

| In `en.json` | Means | Example |
|---|---|---|
| `{0}`, `{1}` | the first, second value | `"{0} of {1}"` → `"{1} 中的 {0}"` |
| `{name}`, `{count}` | a named value | `"Removed {name}"` → `"已移除 {name}"` |
| `{count:N0}` | a number with a format (`N0`: digit groups, no decimals) | `"{count:N0} icons"` → `"{count:N0} 个图标"` |
| `{{` and `}}` | a literal `{` and `}` | `"{{ ... }}"` shows `{ ... }` |

Don't translate what is inside the braces, don't add placeholders English does not have, don't drop one (except
`{count}` in a plural form, below). Product and file names in a text (Kiln, Enshrouded, Nexus Mods, ShroudForge,
`kiln.json`, `KILN_LAUNCHER_NO_WEBGL`) stay as they are.

## Plurals

Where English changes with a number it has a pair of keys:

```json
"filesRead": { "one": "{count} translation file read", "other": "{count} translation files read" }
```

Fill the forms your language uses (the CLDR plural categories: `zero`, `one`, `two`, `few`, `many`, `other`).
Chinese, Japanese and Korean have one form: write only `other`.

```json
"filesRead": { "other": "已读取 {count} 个翻译文件" }
```

## Keep labels short

Buttons, tabs, menu entries and column heads have little room. For Chinese, Japanese and Korean aim for about **half
the English character count** on those (`"Settings"` → `"设置"`, `"Check for updates"` → `"检查更新"`). Sentences in
hints and notices can be as long as they need.

`common.confirmWord.*` are words a player types to confirm a risky action ("uninstall", "delete"): make each one a
single short word that is easy to type with your language's keyboard or input method.

## Test your translation

**Launcher** (no build needed):

1. Settings, Language shows the folder under "Your translation files": `locales` in the launcher's data folder
   (by default `%LOCALAPPDATA%\Kiln\locales` on Windows, `~/.local/share/kiln/locales` on Linux). Make it if it is not
   there yet.
2. Put your file there: `locales/zh-CN.json`.
3. In Settings, Language, press the reload button next to "Your translation files" (or restart the launcher), then
   choose your language under Launcher. It applies at once.

A file there wins over the launcher's built-in file of the same language, so you can test changes to a translation
the launcher already ships. A broken file is named under the reload button with what is wrong.

**In game:** put the file in `<game>/kiln/locales/` and choose the language in game (Mods, Kiln settings, Language) or
in the launcher's Settings, Language, "Kiln in game" (both set kiln.json `ui.language`; "Same as the game" follows the
game's own language). [translating-game.md](translating-game.md) has the details, the CJK fonts and a preview without
the game.

**Check the file** before you send it (with the repository checked out, from `launcher/`):

```
bun scripts/i18n-check.ts path/to/zh-CN.json
```

It lists keys `en.json` does not have (a typo, or a text the launcher no longer shows), values that are not text, and
placeholders that differ from English, and counts the texts not translated yet. The launcher's unit tests run the same
check over every file in `launcher/src/i18n/`.

## Send it

- **Preferred:** a pull request on GitHub adding `launcher/src/i18n/<language>.json` (and, for the game,
  `runtime/locales/<language>.json`). Updates later are pull requests changing the same file.
- **Also fine:** send the file(s) on the Kiln Discord or attach them to a GitHub issue; we add them for you.

When English gains texts, they show in English in your language until you add them; the check script lists what is
missing.

## Credit

Translators are credited in [CREDITS.md](../../CREDITS.md) under Translations, with the language and the name you want
shown, and in the release notes of the release that ships the translation.
