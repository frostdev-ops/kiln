# Support reports

When mods don't load, one file answers the questions a maintainer would otherwise ask over several posts: which Kiln,
which OS and Proton, which game build, what is in the game folder, what the profile loads and what the last start did.

- **Kiln Launcher:** Logs, "Support report" (above the tabs; also with no game found yet, and on a server's Logs page).
  "Save report..." asks where to save `kiln-report-<date>.zip`; "Copy summary" copies a few lines for a forum post.
- **kilnctl** (servers, manual installs): `kilnctl report [NAME] [--dir D] [--out FILE] [--print]`. `NAME` is an
  instance; `--dir` a server or client folder; neither: the client Steam knows. `--print` writes `summary.txt` to the
  terminal instead of the zip.

Both only read. Nothing is written to the game folder, Steam's files are only read, the game may be running.

Code: `crates/kiln-manage/src/report.rs` (`build`, `redact`, `write_zip`), the Tauri commands `report_save` and
`report_summary` (`launcher/src-tauri/src/commands/logs.rs`), `launcher/src/components/SupportReport.tsx`,
`runner/kilnctl/src/report.rs`.

## What is in the zip

| Path | What |
|---|---|
| `summary.txt` | readable in a post: findings, then the system, the game, Kiln in the game, the profile, the last start, sections that could not be read, the file list, what was left out |
| `report.json` | the findings and the unreadable sections as JSON (`{format, written, tool, findings: [{id, level, text}], errors}`) |
| `game/kiln/logs/kiln.log` | the last 1 MiB |
| `game/kiln/logs/proxy.log`, `game/kiln/logs/eml.log`, `game/shroudlink_client.log` | the last 192 KiB of each, when present; addon initialization, input and menu-state diagnostics use the same path/name/Steam ID/IP redaction as Kiln's logs |
| `game/kiln/logs/greeneye-loader.log`, `game/greeneye/loader.log`, `game/greeneye/loader.second-copy.log` | kiln-greeneye's log and Greeneye Loader's own, the last 64 KiB of each, when present |
| `greeneye-mods/<id>/*.log`, `greeneye-mods/<id>/*.status` | each Greeneye mod's own log and status files (top level of its folder, at most 4 per mod and 12 mods, the last 64 KiB of each): a Greeneye mod writes what it found and patched only there (The Worst Map Mod: `unlock_map_markers.log`, `UnlockMapMarkers.status`). The folders are those `kiln/state/greeneye.json` names and those in `greeneye/`, read only inside `<game>/greeneye/` or the launcher's `library/greeneye/` (resolved, no links); any other folder is left out with a note (`greeneye-log-skipped`). Its settings (`.ini`) and data are not read |
| `game/kiln/crash-reports/<newest>` | the last 64 KiB of the newest crash report |
| `game/kiln/state/*.json` | every state file (boot, mods, native, last-server, hooks, each engine's), at most 1 MiB each |
| `game/kiln/profile.json`, `kiln/chain.ini`, `kiln/kiln.json`, `kiln/.install.json`, `dinput8-chain.ini` | as they are, redacted |
| `launcher/launcher.log` | the last 192 KiB of `<data>/logs/launcher.log` |
| `listings/game-folder.txt` | the game folder's top level: type, size, modified, name |
| `listings/kiln-folder.txt` | `kiln/`, two levels deep |
| `listings/mod-folders.txt` | each engine's mods folder (`kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods`, `kiln/eml/mods`, `mods`, `greeneye`, `shrouded_engine_tweaks/mods`, ShroudForge's, `shroudlink_mods`, `ember/mods`), two levels deep |

All files together are cut at 6 MiB before compression (the zip is usually well under 1 MiB). A listing stops at 2000
lines. A file that is cut says so in its first line.

### summary.txt

- **System:** OS (Windows edition, version and build from the registry; on Linux `/etc/os-release` and the kernel), a
  Steam Deck (the launcher's own detection; kilnctl: DMI `Jupiter`/`Galileo`, SteamOS, `SteamDeck=1`).
- **Game:** the folder (redacted), client or server, platform, Steam build, the Proton prefix, the compat tool from
  Steam's `config.vdf` and the prefix's `version` file, Steam's launch options, running or not, the exe's sha256 and
  whether `kiln/sigdb/builds/<side>/<sha256>.json` says Kiln verified this build.
- **Kiln in the game:** the install receipt, each DLL under a proxy name next to the game and what it is
  (`engines::scan_proxies`), `kiln/chain.ini`.
- **Profile:** `kiln/profile.json`: name, generation, drop-ins, each mod (kind, id, version, on/off).
- **The last start:** `boot.json` (Kiln, side, hooks, policy, profile mode, safe idle, chain, plugins, errors),
  `mods.json` and `native.json` (each mod's state and reason), each engine plugin's state file (its state word and
  each mod's state and reason, read as on Logs > Engines), the last boot in kiln.log (`logs::boot_state`), the newest
  crash report.

### Findings

Each is one plain sentence. `problem` explains why mods don't load, `warning` may, `note` is context.

| id | When |
|---|---|
| `not-installed` | no install receipt and no `dinput8.dll` (problem); Kiln's `dinput8.dll` without a receipt (note) |
| `proxy-not-kiln` | `dinput8.dll` is another engine's or an unknown DLL, with or without Kiln's receipt |
| `files-missing` | files of the receipt are gone (names them; the usual cause is an antivirus quarantine) |
| `files-changed` | files of the receipt have another sha256 (`install::modified_files`) |
| `not-loaded` | the game runs and boot.json is not from this start (`launch::boot_is_current`); or the last launcher start (`<data>/installs/<id>.launch.json`) is newer than boot.json; or the game has a log but Kiln wrote neither proxy.log nor boot.json. Under Proton without the dinput8 override the sentence names the launch option (`game::launch_options_info`) |
| `proton-override` | Kiln is in a Proton game, nothing says it failed, and Steam's launch options have no dinput8 override |
| `unknown-build` | no build record for the exe's sha256 (problem) or one that is not verified (warning) |
| `safe-idle` | boot.json's `safeIdle` |
| `own-loader` | an engine plugin's state file says it stood aside (`phase: aside`, `mode: self-hosted`, `steppedAside`) |
| `loader-in-folder` | another engine's own loader under a proxy name the game loads (EML's `dbghelp.dll`, SET's `powrprof.dll` ...): a warning on Windows (it always runs), a note under Wine/Proton (only with an override). Left out when that engine's plugin already said it stood aside |
| `unlisted-mods` | mod folders in the game folder that the active profile does not load (drop-ins off for Kiln's kinds and EML; a `mods.<engine>` list for the compat-hosted engines; ShroudForge and SET always read their folders) and that the profile does not list itself |
| `mod-failed` | `failed` in mods.json, native.json, boot.json's plugins, a chain entry `failed`/`missing`, or a mod in an engine's state file whose state says fail/error/refused; with the reasons |
| `hosted-mods` | a note: Kiln mods `skipped` in mods.json with `side 'server': loads while this client hosts a world`: they start with the player's own world (single-player or co-op), so `skipped` in the menu is expected (docs/kiln/client.md, "Mods and sides") |
| `hosted-off` | a warning: the same with kiln.json `"mods": {"hosted": false}`: they never start in this game |
| `crash` | there is a crash report (the newest is in the zip) |
| `profile-invalid` | `kiln/profile.json` does not parse, or the last start found it invalid |
| `newer-mods` | Kiln mods of the profile built against a newer Kiln (`game::newer_mods`, the Play page's "Update Kiln") |
| `no-game` | the report was made without a game (note) |

## Privacy

Every text goes through one function, `report::redact`:

- the home folder becomes `<home>`, in Linux form (`/home/me`), Wine form (`Z:\home\me`), Windows form
  (`C:\Users\Me`), with `/` or `\` (also doubled inside JSON), case ignored;
- the machine name becomes `<host>` (not when it is the distribution's name, as on a Deck named `steamdeck`);
- the user name, the home folder's last component and the Steam account names in `loginusers.vdf` become `<user>`,
  also inside other words (`me-pc`, Wine's `C:\users\me`); a name of 3 characters or fewer only as a whole word, a
  common word (`user`, `steam`, `kiln` ...) only inside the home folder; Steam display names become `<steam-name>`
  (whole words);
- SteamID64s, `userdata/<n>` and `[U:1:<n>]` become `<steamid>`; IPv4 addresses other than `127.x` and `0.0.0.0`
  become `<ip>`.

JSON files also go through `report::redact_json`: string and number values of fields named like a secret or an address
(`token`, `password`, `secret`, `apiKey`, `cookie`, `credential`, `private`, `auth`, `key`, `ip`, `host`, `endpoint`,
`nonce`, `*address`, `*url`, `*steamId`) become `<redacted>`. A JSON file that does not parse or was cut loses the
values of such fields line by line.

Never read into the report: saves and world data (`kiln/data/`), `kiln/config/` and the profile's config folder (mod
settings, `network.json`), `kiln/data/kiln-admin/`, the contents of any mod (only names, sizes and times in the
listings), Steam's files other than `config.vdf`'s compat tool, the launch options and `loginusers.vdf` (for the
names to replace). `summary.txt` ends with this list.

What redaction cannot catch: a character or server name the player chose that appears in a log line, and 8.3 short
names that cut the user name (`FROSTL~1`).

## When things are broken

The report never fails on the game's state. No Kiln, no game folder, a corrupt profile, an unreadable file, a huge log,
the game running: each section that cannot be read is listed under "Not readable" with why, and the rest is written.
Only writing the zip itself can fail (`report_save` rejects with the I/O error).
