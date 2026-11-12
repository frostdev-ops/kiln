# The client pass

Some of what Kiln does can only be confirmed by a player in the real game: a flash on a hit, a sound, a menu that
opens, a launcher step on a Steam Deck. The client pass is the list of those things as guided checks, in one test mod,
`mods/tests/client-pass` (id `client-pass`, side `both`). Each check sets its scene where code can (gives items,
spawns creatures, plays an effect, sets the time), tells you what to do and what to look at, asserts what code can read
by itself, and asks one yes/no question. You answer with a key. Every answer is written to a file, so a pass leaves a
record that a script turns into a summary.

The pass covers the lines of the status page that wait for a game test: the 18 rows of "Built, game test pending", the
dev.22 and dev.21 client checklists (11 items each), and two lines new in 0.2.0-dev.25 (the achievement guard, and
Kiln's unload when the game quits). `mods/tests/client-pass/rows.json` maps each line to its checks.

> **Status.** The pack builds and its mapping is tested offline. **None of its checks has run in a real game yet**, and
> the check runner itself has run only on dedicated lab servers (see `modding/testing.md`, "Guided checks"). The first
> pass is also the first test of the pack: a check whose scene does not appear (a creature that is not spawned, an item
> that is not given) shows a failed assertion; answer fail and say what you saw.

## The mods to load

Build the pack with `dotnet build mods/tests/client-pass` and add it to the game like any mod. It uses Kiln's sample
and test mods for content instead of copying them. A check whose mod is missing says which one to add and asks you to
press skip: a skip there does not count against the feature. Build each sample with its own steps (several need their
assets built first: their descriptions say so).

**Content profile** (sessions 1 and 2):

| Mod id | Folder | Used by |
|---|---|---|
| `client-pass` | `mods/tests/client-pass` | every check |
| `rendersample` | `mods/samples/RenderSample` | drawing |
| `vfx-sample` | `mods/samples/VfxSample` | VFX spawn shapes, parameters, a game VFX by name |
| `shader-sample` | `mods/samples/ShaderSample` | particle shaders |
| `materialsample` | `mods/samples/MaterialSample` | material reskin (the workbench's wood) |
| `blocks-more-test` | `mods/tests/blocks-more-test` | status icons, tools, piercing, props, sound blocks, VFX on a look, saves |
| `frostbolt-block` | `mods/samples/FrostboltBlock` | Frostbolt's area damage, frost on hits |
| `item-blocks` | `mods/samples/ItemBlocks` | a wand's attacks, armour's look |
| `ability-blocks` | `mods/samples/AbilityBlocks` | an ability on G |
| `creature-ai` | `mods/samples/CreatureAi` | who fights whom, the caller |
| `creature-blocks` | `mods/samples/CreatureBlocks` | a skinned creature from glTF |
| `retarget-test` | `mods/tests/retarget-test` | retargeted animations |
| `npcsample` | `mods/samples/NpcSample` | an NPC and a quest in the game's UI |
| `rules-sample` | `mods/samples/RulesSample` | `/flight` |
| `world-programs` | `mods/samples/WorldPrograms` | a world program with a player |
| `cosmetic-sample` | `mods/samples/CosmeticSample` | session 2 only: the hood and the cape |

**Frameworks profile** (sessions 3 to 5): `client-pass` and one mod of each framework you have: an EML mod, a
ShroudForge mod (one that writes its own settings helps `launcher.kept-mod-files`), a Greeneye mod, Flame (Creative
Mode), a Shroudtopia mod, an Enshwick pack, shroudlink's singleplayer zip, kiln-emf with an EMF mod that has portraits
and a chat command, and XHL-Core. A framework you do not have: skip its check.

**Session 6:** `client-pass` alone, on a new character (see [the cheats group](#the-cheats-group)).

**Item data** (a session of its own, on a dedicated server you join): a lab server with `instances-test`
(`mods/tests/instances-test`) deployed, and your game with the content profile's `client-pass` loaded. Its 21 checks
(area "Item data") run on the server; the instruction and the verdict are on your screen. The `.before` / `.after`
pairs span a leave, a closed game, a server stop and a restart, so the session needs someone who can stop and start
the lab server (`tools/lab/lab.sh stop|start`). The pair that matters most is `instances.crash-swap`: two identical
marked swords, swapped by hand, the game closed without the menu, then rejoin.

## Keys

| Key | Does |
|---|---|
| Ctrl+Shift+F5 | opens the check list (by area, with each check's last verdict and why one would be refused) |
| Ctrl+Shift+F6 | pass |
| Ctrl+Shift+F7 | fail, then type one line and press Enter (Escape records the fail without a note) |
| Ctrl+Shift+F8 | skip |

Rebind them in the Kiln menu, Keys. The current instruction stays on the HUD line with the list closed, so you can play
the check. "Run" starts one check, "Run all" runs them in order; Stop ends a check without an entry. In the main menu
nothing runs: checks that ask about the main menu or the launcher are answered once you are in the world.

The samples have keys of their own on other chords: the VFX sample Ctrl+F6 to Ctrl+F11, the shader sample Ctrl+Alt+F6
to Ctrl+Alt+F8, the render sample Ctrl+J, Ctrl+Shift+J and Ctrl+Alt+J. The checks play their effects themselves, so you
need none of those keys except where an instruction names one.

## The sessions, in order

A session is one start of the game with one set of mods and one kind of world. The checks' order follows the sessions
(order = session x 100 + place), so "Run all" in session 1 runs session 1's checks first. In a later session, start at
that session's first check.

| Session | World and mods | Before you start | Checks |
|---|---|---|---|
| 1 | hosted world of your own (not your main character's world if you mind given items and spawned Vukahs); content profile | the launcher steps of checks 101 to 105 (they say what); main menu: MODS | 101-138 (37): launcher, menus and hooks, drawing, VFX, shaders, blocks, creature AI, models, gameplay with a player, creative mode; last: `core.unload-quit` part 1, then quit with Esc, Quit |
| 2 | the same world, hosted again after the restart; content profile plus `cosmetic-sample`; an overlay that hooks the graphics on (ReShade, vkBasalt or MangoHud) | switch the overlay on | 201-204: the overlay, what session 1 placed, the hood and the cape; last: `core.unload-quit` part 2, then Alt+F4 at the main menu |
| 3 | hosted; frameworks profile, first start with its EML mods | add the frameworks' mods; XHL-Core's Load after Kiln and ShroudForge on; drop shroudlink's singleplayer zip | first: `core.unload-quit` part 3; 301-313 (13): EML first start, Remove refused while running, each framework, EMF portraits and chat, XHL loaded late |
| 4 | hosted; the frameworks profile saved under a second name with XHL-Core's late load off | after session 3: Remove the EML mod and add its archive again; switch the mod with its own files off and on; save the profile under a second name, change the switch, load it | 401-404: Remove after closing, `nothing changed`, kept mod files, the profile's own rules |
| 5 | hosted; the same profile, ShroudForge's own `dbghelp.dll` in the game folder, no DLL override (Linux) | uninstall Kiln in the launcher and install it again; put the file in | 501-502: the profile after a reinstall, mods running next to the folder's loader (take the file out afterwards) |
| 6 | hosted; `client-pass` alone; a **new throwaway character** named `KilnCheck…` | read [the cheats group](#the-cheats-group); answer its question yes only if your account has every level achievement up to level 30 | 601: `guard.proof`, level by level until the guard sees a report (at most 30) |

Sessions 1 and 2 host the same world: session 2 asks about the Lantern and the chest you left in session 1. The
cosmetic sample is kept out of session 1 because hosting a world with it once crashed the game (status page, "Open in
the game"); if session 2 crashes while the world loads, that is a finding to report, and session 1's verdicts are
already in the ledger.

`core.unload-quit` runs in three parts over three starts, because its answer is only known after the game has closed:
part 1 (last check of session 1) asks you to quit with Esc, Quit; part 2 (last check of session 2) reads what Kiln
wrote at that exit, from `kiln/logs/kiln.prev.log` (Kiln renames the last log when it starts), and asks for Alt+F4 at
the main menu; part 3 (first check of session 3) reads that exit and takes the verdict. Each time, watch Steam: within
about 15 seconds after the window closes it must stop showing the game as running. Parts 1 and 2 end with skip; their
assertions are in the ledger with them.

## The cheats group

A check tagged `cheats` changes what the game may report to Steam as an achievement: level, experience, skill points
or knowledge. **A Steam achievement cannot be taken back.** The game reports achievements from the loaded character's
saved state, so this applies to test saves and local copies too.

- **The achievement guard has not run in a real client yet.** It is built, its signature matches the current game, and
  a host test with a stand-in shows reports dropped and counted, but nobody has seen it drop a real report. Do not rely
  on it to protect a Steam account until the release notes say the `guard-proof` check has passed. That is why
  `guard.proof` comes first, and why it only reaches achievements your account already has.
- **Use a throwaway character.** Make a new character named `KilnCheck…` for the cheats group, raise nothing on your
  real characters, and delete the character when the group is done. Before every `cheats` check the runner asks
  whether the character you play is such a character; Kiln cannot read a character's name, so the name only makes the
  question easy to answer.
- **Never load that character again in a game without Kiln.** The guard stays on until the game exits. A character
  changed by a `cheats` check, loaded later in a game where the guard is off, can unlock the achievements its saved
  state reaches.
- **`guard.proof`** raises the character one level at a time until the guard sees the game report an achievement, at
  most to level 30, and grants nothing else. The game reports an achievement only once its knowledge is set, and the
  level achievements sit at certain levels, so most levels report nothing; the check stops at the first level whose
  report the guard dropped. For an account that already has every level achievement up to level 30 this is harmless:
  the game asks Steam first and skips what the account already has, while the report still reaches the guard. Before
  it runs, the check asks you to confirm exactly that. If your account does not have them all, answer no: the guard
  then stays unproven on this build and the other `cheats` checks stay refused. A character already at level 30 or
  above is not changed, and the check fails; use a new one.
- **What to watch.** The check shows the level it has reached ("level 7 of at most 30, no report yet"). Once the guard
  sees a report: the game's own log (`enshrouded.log` in the game folder) must get no new line starting with
  `[online] Achievement`; kiln.log gets `steamguard: dropped a Steam achievement/stat report`; the guard's dropped
  counter goes up (the check shows the numbers and the level reached). The check also watches the game's log itself and
  asserts that no such line came. If level 30 is reached with no report, the proof is inconclusive: press fail. The
  runner records the check as failed if the counter did not go up, whatever you answer.
- **Where the proof lands.** An entry in `kiln/state/client-checks.json` for `guard.proof`, tagged `guard-proof`, with
  verdict `pass`, for that game build (sha256) and Kiln version. Until its newest entry on a build is a pass, the runner
  refuses every other `cheats` check there; a fail or a skip closes that gate again. The pack has no other `cheats`
  check today: none of the lines it covers needs a level, experience, skill-point or knowledge command.
- **What the guard does not cover:** a player whose game has no Kiln, another mod's or loader's own calls into Steam,
  and an achievement reported before the guard went on.
- **Kiln.Quests' `/questadmin` commands are not covered.** Complete, advance and step can grant experience or knowledge
  through quest rewards; start/fail actions and signals can too. Do not use them for a protected test. Kiln.Admin's
  routed bridge patch guards `modquest_set` and `modquest_signal`, not these service-library commands. Rewards paid to
  a player who joins later are also not covered.

## What one player on one Linux PC cannot do

These checks come last in the list, each tagged so you can see them at a glance.

| Group | Tag | Checks |
|---|---|---|
| A second player | `two-players` | 901 `chat.emf-bridge-two`, 902 `glider.seen-by-other`: one hosts, one joins, both with Kiln |
| A Windows PC | `windows` | 911 EML's own `dbghelp.dll`, 912 a non-ASCII user name, 913 a native Windows dedicated server's new item (joined), 914 `nxm://` links back to Vortex, 915 `core.unload-windows` |
| A Steam Deck | `steam-deck` | 921 "No Kiln" in the launcher, 922 the launcher's Steam Deck mode |
| Another host | `other-host` | 931 a remote server with server mods (joined), 932 the server pack on a rented host, 933 an EML mod where `/home` links to `/var/home` (Fedora Atomic, Bazzite) |

`core.unload-windows` runs like `core.unload-quit`, after one step: on native Windows the exit link is off by default
in this release; set `<KEY: core names it in its report>` to `"on"` in `kiln/kiln.json` first.

Checks tagged `launcher` are done (partly) in the launcher or on the desktop; the check only shows the steps and takes
your answer.

## The ledger and the report

Every verdict is appended to `<game>/kiln/state/client-checks.json` (schema `schema/state-client-checks.schema.json`):
the check, its mod and version, Kiln's version, the game build, your verdict and note, the time, the assertions and the
kiln.log lines written during the check. Nothing private goes in: user names, home paths, Steam ids and IP addresses
are replaced. A check run again adds an entry; the newest one counts.

```sh
python3 tools/client-checks/report.py <game>/kiln/state/client-checks.json
python3 tools/client-checks/report.py <game>/kiln/state/client-checks.json --rows mods/tests/client-pass/rows.json
python3 tools/client-checks/report.py <ledger> --rows mods/tests/client-pass/rows.json --json
```

It prints passed, failed, skipped and never run per area, then each failed check with your note and its failed
assertions, and with `--rows` every status line with the state of its checks (`passed` when all its checks passed,
`failed` when one failed, else `open`). A pass whose automatic assertions failed is printed as `passed*` and counted on
a line of its own: look at those, the scene may not have been what the instruction said. It exits with 2 when it cannot
read the ledger or the ledger has another format. To share a result, send the ledger file or the `--json` output.
