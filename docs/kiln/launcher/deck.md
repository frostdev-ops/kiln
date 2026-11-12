# Steam Deck mode

The Kiln Launcher's controller UI for playing modded Enshrouded on a Steam Deck: in Game Mode (the launcher added to
Steam as a non-Steam game) and in Desktop Mode. Same app and download as the desktop UI; a separate UI, not a second
program.

Code: `launcher/src/deck/` (frontend), `launcher/src-tauri/src/deck/` (detection, the gamepad reader, the folder
browser), `launcher/src-tauri/src/commands/deck.rs` (commands). Contracts: [contracts.md](contracts.md) section 9
(`deck_*`, `AppInfo.deck`, `uiMode`), section 10 (`kiln://gamepad`).

**Official 0.2.0-dev.22 (as in dev.20 and dev.21):** the Nexus Catalog, account controls and Mod Manager Download links are disabled on Deck
and desktop while OAuth approval is pending. Download mod archives in a browser and add them from Downloads.
The Catalog descriptions below apply only to an enabled, approved future build; personal API keys are not a fallback.

## How Deck mode is chosen
Decided once at the start, before anything renders (`src/deck/mode.tsx`, `UiModeGate`), in this order:

1. **The command line, for this start:** `--deck` or `--desktop` (the last one given wins), else
   `KILN_LAUNCHER_UI=deck|desktop`. In Steam: the launcher's Properties, Launch Options.
2. **The setting** `uiMode` in the launcher's `settings.json`: `"deck"` or `"desktop"`. Set in either UI: Deck
   Settings, "Start in" (Auto, Deck mode, Desktop); desktop Settings, the "Steam Deck" card.
3. **Detection** (`app_info.deck.detected`, `src-tauri/src/deck/detect.rs`), any one of:
   - env `SteamDeck=1` (Steam sets it for what it starts on a Deck, Game Mode included) or `SteamOS=1`;
   - env `XDG_CURRENT_DESKTOP` naming `gamescope`, or `GAMESCOPE_WAYLAND_DISPLAY` set (a gamescope session);
   - `/sys/class/dmi/id/product_name` `Jupiter` (Steam Deck LCD) or `Galileo` (OLED): this also catches Desktop Mode,
     where Steam's variables may be missing;
   - `/etc/os-release` `ID=steamos` (SteamOS on other handhelds).
4. Otherwise the desktop UI.

A Deck start lands on `#/deck`; the desktop shell (and its WebGL fire) never mounts. A deep link (`#/settings`,
`#/deck/mods`) is kept as it is. Switching later is explicit: Deck Settings, "Desktop UI" (this start), or the desktop
Settings card's "Open Deck mode".

The Deck UI makes the window fullscreen (`deck_fullscreen`; Game Mode is fullscreen anyway) and is laid out for
1280×800: 1 rem = 16 px there, and the root font scales with the window (`min(w/1280, h/800)`), so a docked Deck at
1920×1080 gets the same layout, larger.

## The Hearth on the Deck
The Deck UI keeps the desktop's look and its effects:

- **The fire** (`src/deck/Fire.tsx`) is the desktop stage's own WebGL shader (`shell/Stage.tsx` exports it): the brick
  wall, the pointed arch with its voussoirs and gold keystone, the chamber and its flames, the floor, the haze and the
  embers. On Home the mouth stands where the Play button is; on every other tab it springs into the pilot light at the
  top right. The camera leans a little toward the focused control (there is no pointer). Heat follows the game: unlit
  without a game, a flare while it starts, embers while it runs, hotter while Play is focused.
- **Budget** as on the desktop: at most 1000 px wide, ~30 fps on Home, ~9 fps on other tabs, full rate only while the
  mouth moves or the heat changes; a still frame while the game runs or the launcher's window has no focus (the GPU
  is the game's then), nothing while hidden.
- **Off**: Deck Settings' Animated fire (the same setting as the desktop's), `KILN_LAUNCHER_NO_WEBGL=1`, or no WebGL.
  The CSS mouth (the same gradient arch as the desktop's fallback) and the wall glow take over.
- **Motion**: screens rise in, rows arrive in a short stagger, the gold diamond slides under the tab, a lit mod's
  flame pops and flickers, dialogs and toasts rise, Hold fills over 1.2 s, the Play arch's label lifts on focus.
  `prefers-reduced-motion` makes everything instant (and the fire a still frame).

## Controls
| Button | Does |
|---|---|
| D-pad, left stick | move focus to the nearest control that way (repeat while held: after 380 ms, then every 120 ms, 70 ms after 1.5 s) |
| A | press the focused control (hold it for 1.2 s on a "Hold to …" button) |
| B | back: close the dialog, go up a folder, else to Home |
| X, Y | the screen's own actions (the hint bar says which) |
| L1 / R1 (LB / RB) | previous / next tab (not under a dialog) |
| Start (☰) | the menu: every screen, Quit |
| View (⧉) | Help |
| right stick | scroll |
| touch | everything is a real button: tap it; the hint bar's buttons are tappable too |

Spatial navigation (`src/deck/focus.ts`): only controls whose centre lies beyond the focused one count; the nearest is
the gap along the direction plus three times the gap across it, so lists move row by row and grids stay in their
column. No wrap-around at the edges. Each screen remembers what was focused when you left it. Dialogs are layers: the
screen under one is inert, B closes it and focus returns to what opened it. The ring is a 3 px gold outline with a warm
glow; the Play arch glows along its shape.

The hint bar at the bottom shows the glyphs for the current screen: Steam Deck style (dark faces, L1/R1, ☰, ⧉) on a
Deck or with a Valve controller, Xbox style (coloured letters, LB/RB) otherwise; Settings can pick one.

### Input sources
All end in the same dispatcher (`src/deck/DeckApp.tsx`):

- **Native reader** (`src-tauri/src/deck/gamepad.rs`, Linux): evdev on a background thread started by
  `deck_gamepad(true)` when the Deck UI mounts (stopped when it unmounts), emitted as `kiln://gamepad`. It rescans
  `/dev/input` every 2 s (hot-plug), keeps devices with `BTN_SOUTH` (gamepads), never grabs them, maps D-pad hats and
  buttons, turns the left stick into directions (press past 0.6, release under 0.4, the larger axis wins), merges
  sources and repeats. Button positions follow xpad, which Steam's virtual controller (what a game gets in Game Mode)
  uses: `BTN_SOUTH` A, `BTN_EAST` B, `BTN_X` (0x133) X, `BTN_Y` (0x134) Y. WebKitGTK's Gamepad API is unreliable, so
  this is the main path.
- **Keyboard** (what Steam Input keyboard templates send): arrows move, Enter/Space = A, Escape/Backspace = B, X or
  Delete = X, Y = Y, Q / PageUp / `[` = L1, E / PageDown / `]` = R1, M / Menu key / F10 = Start, F1 / V = View; Tab
  keeps the browser's own focus order within the top dialog, wrapping at its ends.
- **Browser Gamepad API** (WebView2 on Windows, a plain browser): polled each frame while a pad is connected, standard
  mapping, the same repeat and right-stick scrolling; off while the native reader sees a controller (one controller
  must not press twice), available again after its last device disconnects.

Native events are ignored while the launcher's window has no focus: once the game is in front, its input is its own.
Losing window focus, hiding the window, disconnecting the controller, disabling the button or moving focus cancels
an in-progress Hold action. A disabled focused control returns focus to the current layer's first enabled stop.

## Screens
| Screen | What | Buttons |
|---|---|---|
| Home | the game's name, how many mods are on over every kind ("12 mods on, 15 off"; a profile other than "My mods" is named after it), the Play arch, Kiln's state (Kiln version, "not installed", "update ready", "Game not found"), the last boot in one line ("Booted, no errors", "Booted, 1 failed", "Booted, 2 errors", safe idle, no mods) | A on the arch: Play / Install / Find game; Install Kiln, Update Kiln, Pick folder, One Steam setting (to Help) as the state needs; X play once (without mods, vanilla) |
| Mods | every mod of every kind (the desktop's list, `shell/mods.ts`): the Library's, the loaded profile's, and the mods found in the game folder ("Game folder" with their state); a flame per mod, the count over every kind | A on/off (a mod the profile lacks goes in, on); on a game-folder mod A does its fix (load that kind's game-folder mods, or add it); X remove (confirm; out of the list, its files out of the Library). No profile picker: profiles are in Settings |
| Add mods | the mods the library recognises in Downloads (`deck_browse`: `library_detect` on zips, 7z and rar files, DLLs and mod folders), newest first, "On" when it is on already; other folders and Places (Downloads, Home, Desktop, SD card, USB drives) | A on a mod: `library_add` (import, switch it on in the loaded profile or "My mods", write profile.json); A on a modpack zip: its consent dialog, then `modpack_install`; A on a folder opens it; X up; Y places; B back up |
| Catalog (enabled builds only) | Enshrouded's mods on Nexus Mods ([catalog.md](catalog.md)), Kiln mods first: a row per mod with its author, version, endorsements and state (Installed, Update, EMF/native/DLL); more rows with More. A mod's page: summary, what it needs, Installed or Update ready | A on a mod: its page; there A on Add (premium: downloads, adds to the active profile) or Update, or Open on Nexus Mods (free accounts download on the site); X what is shown (Kiln mods, All Enshrouded, Installed); Y the order (most endorsed, recently updated, name). After an add: OK, Undo, Get <mod it needs>. A Mod Manager Download link shows the same dialog on any screen. Account sign-in uses OAuth in desktop Settings; this tab is hidden in dev.20 |
| Kiln | the runtime in the game: version, when, files, runs on | Install, Update to the newest bundle, Hold to uninstall (1.2 s, the Deck's stand-in for the typed word); the running step and the outcome |
| Help | the Steam launch option for Valve's Proton (state: needed, set, not needed), how to add the launcher to Steam, the controls | D-pad walks the blocks, right stick scrolls |
| Settings | Desktop UI (now), Start in (Auto / Deck mode / Desktop), Button glyphs, Fullscreen (Desktop Mode only), Launcher updates (check, install, restart; the Nexus Mods build instead: New versions, Nexus Mods, A opens the Kiln page there, no update check), Profile (the loaded one; A opens the list: load one, or No profile), Quit; the last line shows the version, what the detection saw and the controller | A |

Text input is avoided everywhere. Where Steam needs typing (launch options), Help says Steam + X opens the keyboard.
Notices are a short title and buttons.

### The Steam rule
The launcher never writes Steam's files: no `shortcuts.vdf`, no `localconfig.vdf`, no launch options. Help gives the
steps instead:

- **Proton:** if Kiln does not load, Enshrouded's launch options can use `WINEDLLOVERRIDES="dinput8=n,b" %command%`
  to load Kiln's `dinput8.dll` (unnecessary with GE-Proton 11-11 and newer, and some recent Valve builds). The state comes from `game_status.launchOptions` (a
  read-only look at Steam's config). Steps: Steam button, Library, Enshrouded, the gear (or ☰), Properties, General,
  Launch Options; Steam + X for the keyboard. Measured 2026-11-07 (desktop, outside Steam's launch, Proton's loaddll
  trace): Proton Experimental 11.0-20261027b and UMU-Proton 10.0-4 load the game folder's `DINPUT8.dll` as native
  without the override, so which Valve Proton builds still need it is not settled; the setting is harmless where it
  is not needed.
- **Kiln not loading:** a running game whose play-once flag is still there 30 s after its start (or a non-vanilla launcher start
  with no boot.json a minute into the process's run) shows **No Kiln** on Home and a "Kiln is not loading"
  button to Help, whose "Kiln not loading?" block says what to send (`kiln/logs/proxy.log`, `kiln/logs/kiln.log`,
  `~/.local/share/kiln/logs/launcher.log`, Proton version, Game or Desktop Mode). Missing, invalid or future process
  timestamps use when this session first saw the process. A fresh boot wins over a leftover flag; vanilla starts
  remembered by this launcher show **Vanilla**, without a missing-Kiln warning.
- **Kiln in Game Mode:** Desktop Mode (Steam, Power, Switch to Desktop), open Steam, Games, Add a Non-Steam Game to My
  Library, browse to the Kiln Launcher AppImage, Add Selected Programs. Back in Game Mode it is under Library,
  Non-Steam, and Deck mode opens by itself (`--deck` as its launch option forces it). Controller layout: Steam's
  default Gamepad template.

## Testing
- `cargo test -p kiln-launcher deck` (`CARGO_TARGET_DIR=.out/deck/cargo`): detection with fake env, DMI and
  os-release files (Game Mode env alone, Desktop Mode by DMI, SteamOS, flags); the evdev mapping (xpad faces, hats,
  stick hysteresis on signed and unsigned ranges, triggers), merging and repeat timing, unplugging, the right stick's
  throttle; the folder browser on temp folders (zips, unpacked mods, a folder holding a mod, junk, Places, XDG).
- `bun x playwright test tests/catalog.spec.ts` (Deck part): the Catalog tab with the pad, X and Y, a mod's page, Add,
  the added dialog with Undo and Get, a free account's Open on Nexus Mods, an nxm link's dialog.
- `bun run test:smoke` (`tests/deck.spec.ts`, 1280×800, mock mode): the UI choice (Game Mode env, `--deck`,
  `--desktop`, the setting), spatial focus, every screen reachable and left with B, Start menu, View, keyboard, the
  browser Gamepad API with a fake pad, touch, Play, adding from Downloads, browsing Places and folders, the modpack
  consent, Mods on/off/remove, the hold to uninstall, Find game by hand, Help, the fire (on in Deck mode, the pilot
  light away from Home, Settings turns it off and the CSS arch takes over).
  `KILN_DECK_SHOTS=<dir>` saves a screenshot of every screen.
- Mock mode: `?deck=steamdeck` (Game Mode env and DMI), `?deck=forced` (`--deck`), `?deck=desktop` (`--desktop`);
  gamepad events from the console: `__kilnMock.emit("kiln://gamepad", {kind: "button", button: "rb", pressed: true,
  repeat: false, device: "x"})`.
- The native reader through the kernel: `cargo test -p kiln-launcher deck::gamepad::tests::uinput_pad_end_to_end --
  --ignored` creates a uinput pad (test vendor 0x1209, no Guide button) and checks what the real reader emits: R1, A,
  X, D-pad down with repeats, the left stick as Left, the unplug. Opt-in: it needs `/dev/uinput` access (an ACL for
  the seat user on most desktops, no root) and creates a system-wide device for a few seconds, so not while a game
  has focus.

### Steam Deck emulation (2026-11-06, no Deck hardware)
- **gamescope**: the real launcher (debug build, assets embedded) inside gamescope 3.16.31 (`--backend headless -W
  1280 -H 800`; the package unpacked, not installed) on this Arch host, screenshots with `gamescopectl screenshot`.
  A first start with a fresh data dir and no Steam variables at all lands in Deck mode from what gamescope sets by
  itself (`XDG_CURRENT_DESKTOP=gamescope`, `GAMESCOPE_WAYLAND_DISPLAY`); with `SteamDeck=1 SteamOS=1` added they show
  too. WebKitGTK and the WebGL fire render through gamescope's XWayland. Shots: `.out/deck/shots/gamescope-*.png`.
- **Virtual controller**: a python-evdev uinput pad drove the same build (in gamescope and on a private Xvfb) through
  Find game (Places, folders, "Use this folder"), the tabs, Add mods listing a real mod zip via the real
  `library_detect`, the left stick and B. launcher.log shows `gamepad: event9 connected: Kiln Deck Test Pad`. The glyphs
  switch to Xbox style for a non-Valve pad, as designed. Found and fixed on the way: a dialog whose content loads late
  got no first focus; focus was lost when what opened a dialog disappeared; a pad whose node appeared before udev
  granted access was never retried.
- Not emulated: Steam's own Game Mode shell (the Steam button, the overlay keyboard, Steam Input's templates and its
  virtual controller), the Deck's DMI (unit-tested with fake files), the touchscreen (Playwright touch only), real
  performance on the Deck's APU.

## Not done yet
- No real Steam Deck has run it: the checklist below is for the first one.
- The modpack offer after a server refused you (`kiln://last-server`) shows only in the desktop UI; a modpack zip in
  Downloads works in Deck mode.
- Not in Deck mode: mod editing beyond on/off/remove (order, saving a profile under a new name, configs), saves and backups, logs,
  servers and admin, rollback, "switch from ShroudForge" (typed), runtime bundle import, the save editor. Settings has
  "Desktop UI" for those.
- The game-art extraction (`GameArtAuto`) does not run in Deck mode (only the save editor and the admin map use it).
- No on-screen keyboard of our own: no screen needs typing. The Catalog has no text search in Deck mode (X and Y
  filter and sort). In an enabled build, Nexus account sign-in uses OAuth in desktop Settings; dev.20, dev.21 and dev.22 have neither Catalog nor account controls.
- Rumble, gyro and the trackpads are not used. Steam's virtual controller sends the Deck's buttons; with Steam
  Input off, the Deck's own `hid-steam` device works the same.
- WebKitGTK inside gamescope drew fine here (below). If a Deck shows a blank window, `WEBKIT_DISABLE_COMPOSITING_MODE=1`
  in the launch options is the usual workaround, but it also turns WebGL off: the fire falls back to the CSS arch.
  (The launcher already sets `WEBKIT_DISABLE_DMABUF_RENDERER=1`.)
- `cargo xwin check` for Windows was not run for the Deck code; the reader is Linux-only by `cfg`, Windows uses the
  browser Gamepad API.

## Deck tester checklist
For a Steam Deck player helping us test. Report each line as OK or what happened (a phone photo helps), plus the last
line of Deck Settings (it names what the detection saw and your controller).

**Setup (Desktop Mode)**
1. Put the Kiln Launcher AppImage somewhere (e.g. `~/Applications`), make it executable (right-click, Properties,
   Permissions, "Is executable").
2. Start it once from the desktop. Does it open in Deck mode (big Home screen, tabs at the top, button hints at the
   bottom)? Is it fullscreen?
3. Add it to Steam: Steam, Games, Add a Non-Steam Game, browse to the AppImage, Add Selected Programs.

**Game Mode**
4. Switch to Game Mode, start "Kiln Launcher" from Library, Non-Steam. Does it open straight in Deck mode, filling the
   screen, with no mouse pointer needed?
5. D-pad and left stick: does focus move where you expect (a gold ring)? Holding a direction: does it repeat?
6. L1 / R1 switch tabs; B goes back to Home; ☰ opens the menu; ⧉ opens Help.
7. Do the button glyphs at the bottom look like the Deck's buttons?
   Home's fire: does it burn smoothly (flames, embers)? Does the fan or the battery notice while the launcher sits on
   Home? (Settings, Animated fire turns it off.)
8. Touch: tap a tab, tap a mod; does it react?
9. Home: does it say how many mods are on and "Kiln …" or "Kiln not installed"? If not installed: Install (A on the
   arch). Did it finish?
10. Help: is the Steam setting shown as "Needed", "Already set" or "Not needed"? If needed, follow the steps on screen
    (Steam + X for the keyboard). Easy to follow?
11. Download a Kiln mod (a zip) in Desktop Mode's browser into Downloads. Back in Game Mode: Add mods lists it? A adds
    it ("Added …")? Mods shows it on?
12. Mods: A turns it off and on; X removes it.
13. Home: Play. Does Enshrouded start from Steam? In the game, is the launcher's input quiet (no tab switching behind
    the game)?
14. After the game: Home's "Last boot" line, does it say "Booted" and whether anything failed?
15. Settings: "Desktop UI" switches to the mouse UI; desktop Settings, "Open Deck mode" comes back.
16. Settings, Quit: does it close cleanly back to Steam?
17. Anything too small to read, hard to reach, or confusing?
