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
- **The fire** (`shell/Stage.tsx`): `--fire` is a non-inherited property set on the two elements that read it, so the
  flicker restyles two elements, not the page under the sheet; while the game runs the loop looks four times a second
  instead of every frame.
- **The bundle**: the browser mock (`api/mock.ts` and its data) is a chunk of its own, loaded only in a plain browser
  or by the development fallback; admin pages, server pages, the save editor and the Deck UI were lazy already.
