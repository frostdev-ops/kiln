# Launcher performance: what it costs and how to measure it

The launcher sits next to a running game for hours. Its idle cost is the backend's watcher and the frontend's polls;
its click cost is the command behind the click. Both have a harness in the repo. Measure before and after a change.

## Backend: `tools/launcher/perf.sh`

```
tools/launcher/perf.sh [--gen] [--iters N] [--out FILE]
```

Two ignored tests in `launcher/src-tauri/src/perf.rs`:

- `perf::generate` builds a fake world in `$KILN_OUT/perf/data` (about 0.6 GB; `KILN_PERF_DIR` moves it): a Steam root
  with the client (Proton prefix with a 3 MB `user.reg`, 60 KB `localconfig.vdf`, 12 save slots), the launcher data dir
  with a Library of 61 mods over every kind (some in two versions), 5 profiles, 8 runtime bundles of 576 files each,
  8 save backups and a local server instance; Kiln installed over an EMF proxy (200 MB, `KILN_PERF_RUNTIME_MB`), three
  more `[chain]` DLLs, `kiln/state/*.json`, a 4 MiB `hooks.json`, a 20 MB `kiln.log`. Only fake files.
- `perf::bench` points the launcher at it (`KILN_DATA_DIR`, `KILN_STEAM_ROOTS`) and times every command the UI calls,
  the polls first, then page loads and clicks, and the parts of the expensive ones (`part:` rows). For each: the first
  call in the process, the median and max of N warm calls, and the CPU time of the whole process (every thread's
  `schedstat`: commands run on the blocking pool). The ranking is by CPU, which a busy machine disturbs far less than
  wall-clock time. At the end a stand-in game (a copy of `sleep` named `enshrouded.exe`, working folder the game's)
  runs, for the "game running" rows. The JSON goes to `--out` (default `$KILN_OUT/perf/bench.json`).

The first run of a fresh data set hashes everything (no hash store yet); run it a second time for what a launcher
restart pays.

## Frontend: `tools/launcher/perf-ui.mjs`

```
node tools/launcher/perf-ui.mjs [--dist DIR] [--out FILE] [--idle SECONDS]
```

A production build (built into `$KILN_OUT/perf/dist` unless `--dist` names one) in mock mode, headless Chromium only.
It reports start (first contentful paint, Play ready, JS fetched), idle time on Play, on Play with the window not
focused and on the Library (main-thread busy, script, style and layout ms per second from CDP, React commits per second
from a DevTools-hook counter, backend calls per minute by command, long tasks), every header route (first visit and
again), and the Library with 600 entries and the Logs page with a 2 MB log (answers planned into the mock). Headless
Chromium draws WebGL in software, which takes the whole main thread, so everything runs with WebGL off; `playWebgl`
and `libraryWebgl` show the fire on (their style numbers are the real signal; the busy time is SwiftShader's).

## What the launcher does to stay cheap

- **Caches keyed by the file** (`kiln_manage::filecache`): a file is unchanged while its size, mtime and (Unix) inode
  and ctime, (Windows) creation time are; a rewrite or replace by rename always changes one of them. Errors are never
  remembered. Users: the sha256s of installed runtime files and bundle zips (kept across launcher starts in
  `<data>/state/file-hashes.json`, written when new hashes came in), bundle manifests (`bundle::read`), PE
  classification (`pe::classify_proxy`), `hooks.json` (`compat::read_hooks`). The launcher's `game_status` also keeps
  the launch-options answer while `user.reg`, the Steam users and their `localconfig.vdf` are unchanged.
- **Process lookups**: on Linux `proc::Finder` reads every process's `comm` and `cmdline` and the rest (state, exe, cwd)
  only for name matches. The watcher, once it found the game, checks only that pid (`proc::still_running`) every
  second; the full scan runs only to see the game start or a pid end.
- **Polls** (`launcher/src/api/hooks.ts`): `usePoll(ms)` polls every `ms` while the window is focused and at most every
  30 s while it is not (the game in front); a hidden window does not poll. Focusing the window again restarts the timer
  at `ms`. Backend events (`kiln://game`, `kiln://last-server`) refresh at once either way. `launch_state` polls only
  while the game runs (or its state is not known yet): a stopped game writes none of its files, and its start, stop and
  state-file changes arrive as `kiln://game`.
- **The fire** (`shell/Stage.tsx`) and **the torch** (`shell/AppShell.tsx`): what changes while things move is written
  only onto leaves, elements with no children that read it: the flicker's `--fire` onto the sheet's lit top edge
  (`.k-sheet-rim`, a 24 px strip) and the Play title, the pointer's `--mx`/`--my` onto the torch's own layer
  (`.k-torch`, moved with a transform, never redrawn), the mouth's spring `--k` onto the mouth's label. The properties
  are registered as not inherited too (`@property`). Before 0.2.0-dev.29.18 the flicker went onto `.k-sheet` and the
  pointer onto `.k-app`: each write repainted the whole page under the sheet (and restyled the whole window, for the
  pointer), which made every page but Play lag in WebKitGTK, most of all when it draws without the GPU (inbox 94). The
  torch runs only while the fire burns: not with Animated fire off, without the GPU, without WebGL or with reduced
  motion; its mousemove listener on `.k-app` exists only then. Reduced motion is followed while the launcher runs (KDE's
  animation speed "Instant" turns it on): the fire stands still, writes no flicker and draws a frame only when what it
  shows changed (`wakeStage`, a resize), asking for no frame or timer meanwhile. While the game runs the loop looks four
  times a second instead of every frame. Behind a sheet, once the mouth is the pilot light, the canvas is only the
  header band (`stageCanvas`, `STAGE_BAND`: 100 px, the header and the sheet's lit edge, the same pixels the whole canvas
  has there), so a frame of the fire repaints that strip and not the page under the sheet; the flicker is written on
  the frames the fire draws. `tests/sheet-perf.spec.ts` counts the writes, the torch's listener and the frames asked
  for. Over Thanksgiving weekend (`shell/thanksgiving.ts`, 0.2.0-dev.31.19) the fire's shader is `FRAG_TURKEY` (about
  twice every day's Play frame on a GPU; `tests/thanksgiving.spec.ts` measures both) and the flicker also goes onto the
  greeting over the mouth (`.k-thanks`, a leaf); a driver that cannot build that shader gets every day's `FRAG`, without
  the greeting (`.k-app[data-turkey]`).
- **WebKit without compositing** (`.k-app[data-render="flat"]`, `Stage.tsx flatDrawing`): WebKitGTK with hardware
  acceleration off (webkit://gpu says "Policy never"; `WEBKIT_DISABLE_DMABUF_RENDERER=1`, which the launcher set
  every time up to 0.2.0-dev.29.18 and from 29.19 only where the DMA-BUF renderer failed (its marker, `--no-dmabuf`),
  or `WEBKIT_DISABLE_COMPOSITING_MODE=1`, software mode) paints every change again on the CPU with everything over and under it, in one
  picture of the window: scrolling repaints the whole list, so its cost grows with the window. WebKit answers
  `(-webkit-transform-3d)` with false there (Chromium and WebKit with compositing: true). Then lit flames and the torch
  stand still and the fire on Play draws about 15 frames a second instead of 30 (`FLAT_PLAY_MS`).
- **Without the GPU** (`.k-app[data-render="software"]`, Linux): no WebGL fire, no torch, and a lit mod's flame stands
  still (`ui/ui.css`): on the CPU every frame of its flicker repainted each flame and its glow, most of the launcher's
  time on a page of lit mods at a HiDPI scale.
- **The bundle**: the browser mock (`api/mock.ts` and its data) is a chunk of its own, loaded only in a plain browser
  or by the development fallback; admin pages, server pages, the save editor and the Deck UI were lazy already.
