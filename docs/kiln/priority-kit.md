# The priority kit: the engine gate on native Windows and on Proton

A tester kit for [GitHub #58](https://github.com/frostdev-ops/kiln/issues/58), part of the Kiln tasks plan
([#45](https://github.com/frostdev-ops/kiln/issues/45)). It measures what Wine can't prove for task-thread writes
([world.md](services/world.md#world-writes-from-task-threads)).

When a Kiln task writes a component, it takes the engine's own write gate between frames. While Kiln holds the gate,
the game's server thread spins at the start of its next frame. Kiln's task workers run below normal priority, so the
holder is raised to above normal for the hold ([engine.md](services/engine.md#the-engines-write-gate-kilninternalengine)).
A preempted holder would otherwise keep the server spinning. That works on paper. It has never been measured on native
Windows, and Wine may ignore thread priorities. The kit answers three questions under CPU contention:

1. **Hold time:** how long a gated write holds the gate, with the priority raise on and off.
2. **Server step time:** the game's step times in the same runs.
3. **Native Windows vs Proton:** whether the two differ.

It also runs the write path's fault test once on real Windows, which shows that faulting reads and writes are
contained (SEH).

## What you need

- A Windows 10 or 11 PC with Enshrouded on Steam, playing natively (not through Proton). A Linux PC with Proton is
  welcome too, as a second data set.
- About an hour, most of it waiting.
- Your own save, the world you play as host. Not a shared or dedicated server.
- The Kiln test build and the kit zip, both linked in #58.

## What it changes

- **In the game:** one byte of one component of your own character, a byte no field of that component uses. The
  component's reflection says which bytes are padding between or after its fields. Only components that Kiln's
  write policy lets a task write qualify: untracked, so never saved or replicated, and plain values only. The mod
  flips one bit of that byte and puts it back at once, many times a second, then reads the component back. Nothing a
  game system reads changes, and nothing is saved. Only the world named in the run file is touched; on any other world
  the mod does nothing.
- **On disk**, under `<game>\kiln`:
  - `native\gate-priority-test\`: the mod;
  - `data\gate-priority-test\run.txt`: its run file;
  - `data\kiln.engine\gate-priority-raise`: the measurement switch, only during the "raise off" run;
  - `logs\gate-priority-test\`: its measurements.
- **The measurement switch** turns the priority raise off for one game start, so the two modes can be compared. It
  is for measurements only and not supported for play. Kiln logs a warning at every start while it is there. The
  collection script deletes it at the end, also when the collection fails.
- **The CPU-load helper** keeps your CPU busy in timed phases and stops by itself. The game may stutter while it runs;
  that is what's being measured.

## Steps on Windows

Before unzipping, right-click the zip, choose Properties and tick "Unblock" if it's there. Otherwise Windows may ask
before it runs the kit's files. Unzip the kit, for example to `Desktop\kiln-priority-kit`. Open PowerShell in that
folder: Shift + right-click inside the folder, then "Open PowerShell window here" or "Open in Terminal". Each command below starts with
`powershell -ExecutionPolicy Bypass -File`, which runs one of the kit's scripts without changing your system's
settings. `powershell` is Windows PowerShell 5.1, which comes with Windows 10 and 11; the scripts need nothing newer
(PowerShell 7's `pwsh` works too). Every script takes `-Game "<the Enshrouded folder>"` if it can't find the game in
your Steam libraries.

1. **The check without the game** (a few seconds):

   ```powershell
   .\tests\world-ecs-fault.exe > fault-test.txt
   ```

   Its last line should read `ecs_fault_win: N checks, 0 failures`. The collection script picks up `fault-test.txt`.
2. **Install** the Kiln test build if you haven't yet (as any Kiln update), then the test mod:

   ```powershell
   powershell -ExecutionPolicy Bypass -File kit.ps1 install
   ```

   If you use the Kiln launcher with a profile, turn on drop-ins for native mods in that profile's settings. The
   install command warns if it finds a profile that doesn't load them.
3. **Name your world.** Start the game, load the world you'll test on and wait about a minute. The mod logs the world's
   GUID and does nothing else yet. Quit to the desktop, then:

   ```powershell
   powershell -ExecutionPolicy Bypass -File kit.ps1 world
   ```
4. **Run 1, the priority raise on** (the normal state):
   1. Start the game and load the same world. Stand somewhere quiet, inside your base for example, and leave the
      character idle. Keep the game window in front.
   2. Within the first minute, start the CPU load in a second PowerShell window:

      ```powershell
      powershell -ExecutionPolicy Bypass -File cpu-load.ps1
      ```

      It runs for 14 minutes: 2 idle, then 3 times 2 loaded and 2 idle. The mod writes for 15 minutes after 30
      seconds of measuring the steps alone.
   3. Wait until the CPU-load window says `Done`, then one minute more. Quit the game.
   4. Collect:

      ```powershell
      powershell -ExecutionPolicy Bypass -File collect.ps1
      ```

      This writes `kiln-priority-kit-windows-raise-on-<time>.zip` next to the scripts. Collect before the next start:
      Kiln starts `tick-stats.jsonl` afresh every time the game starts.
5. **Run 2, the priority raise off:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File kit.ps1 raise off
   ```

   Then repeat steps 4.1 to 4.4. The zip is named `...-raise-off-...`. `collect.ps1` deletes the switch at its end;
   `kit.ps1 status` should then say `Switch: none: the raise is on`.
6. **Send:** attach both zips to #58. If Python is installed, you can also paste what
   `python analyze.py <zip> <zip>` prints.
7. **Undo:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File kit.ps1 uninstall -Logs
   ```

   This removes the mod, its run file, the switch and the measurements. Then put back your usual Kiln build if you
   installed the test build only for this.

   To undo by hand instead (for example if a script won't run), quit the game, then delete these from the game
   folder's `kiln` folder:
   1. `native\gate-priority-test` (the mod);
   2. `data\gate-priority-test` (its run file);
   3. `data\kiln.engine\gate-priority-raise` (the switch, if it's there);
   4. `logs\gate-priority-test` (the measurements, once you've sent them).

   Nothing else on your PC was changed. The CPU-load helper leaves nothing running: it stops at its own time limit, at
   Ctrl+C or when you close its window. The kit's folder and zips can be deleted at any time.

   To check that the raise is back on: `kit.ps1 status` says `Switch: none: the raise is on`, and after the next start
   `kiln\logs\kiln.log` has no line saying `the write gate's priority raise is off`.

If you have time for more, run more pairs: the numbers get better with every pair. You can also try a load of
`-Priority AboveNormal`, or `-Threads` less than your number of logical CPUs.

## Steps on Linux (Proton)

The same steps, with `kit.sh`, `cpu-load.sh` and `collect.sh` from the kit folder. They need bash and Python 3.

```sh
./kit.sh install          # then start the game, load your world, wait a minute, quit
./kit.sh world
# run 1: start the game, load the world, then in a terminal:
./cpu-load.sh             # wait for "Done", one more minute, quit the game
./collect.sh
# run 2:
./kit.sh raise off        # then the same as run 1
./kit.sh uninstall --logs
```

`world-ecs-fault.exe` proves nothing new under Proton; skip it there. `cpu-load.sh --priority above` needs a nice limit
that allows a negative nice value (`ulimit -e`). `collect.sh` records that limit and the Proton version, since both
decide whether Wine passes thread priorities on.

## What goes into the zip

- From `kiln\logs\kiln.log` and `kiln.prev.log`, only the lines the analysis reads: Kiln's start, `kiln.engine`, the
  test mod, kiln.world's writes, kiln.tasks, the tick and faults.
- `kiln\logs\tick-stats.jsonl` and `kiln\state\capabilities.json`.
- The test mod's runs (`kiln\logs\gate-priority-test\run-*`):
  - `run.json`: the target, the mode, the raise and a summary;
  - `writes.csv`: every compare-exchange: its outcome, the gate's hold time, the calling thread's priority;
  - `steps.csv`: every server step's Pre and Post times;
  - `gate.csv`: `kiln.engine`'s gate counters every 10 seconds.

  Each CSV's first line says whether the raise was on or off.
- The CPU-load helper's phase files (`cpu-load-*.csv`) and `fault-test.txt`.
- `system.json`: Windows version or Linux distribution and kernel, Proton version, CPU, cores, memory, power plan or
  CPU governor, the time-zone offset, and whether the switch was set.

Lines that name players (joins, chat) or worlds stay out. Your user name, the computer's name, home folders, Steam ids
(also the account folder under Steam's `userdata`) and world GUIDs are replaced before anything is written.
Open the zip and look before you post it.

## If something goes wrong

Look for `gate-priority-test:` lines in `<game>\kiln\logs\kiln.log`:

| Line | What it means |
|---|---|
| none at all | The mod didn't load. Check `kit.ps1 status`, and the launcher profile's drop-ins (step 2) |
| `no run file ...` | `kit.ps1 install` wasn't run |
| `this world is ..., the run file names ...` | Another world is loaded; `kit.ps1 world` names the newest one logged |
| `kiln.world's compare-exchange writes are off on this build` | The game has updated past the builds Kiln knows; say so in #58 with the line `kiln.engine: ...` above it |
| `... it runs no-op exchanges instead` | No component of your character has a usable padding byte. The run still takes the gate (no write) and is still useful |
| `done (...)` | The summary: outcome counts, hold times, whether the byte is back |
| `done (...)` with `the last change could not be put back` | A write could not be proven, or the world stopped between a change and its restore. The changed bit is in a padding byte of an untracked component, so it is only in memory and never saved: quit the game, and it's gone. Say so in #58 |

If the game crashes, attach the crash report Kiln writes in `kiln\logs` together with the zip.

## The run file

`<game>\kiln\data\gate-priority-test\run.txt`, one `key=value` per line:

| Key | Default | |
|---|---|---|
| `world` | none | The world's GUID (32 hex digits). Without it the mod only logs the running world's GUID |
| `minutes` | 15 | How long the writes run (1 to 120). `seconds` sets the same in seconds, for short checks |
| `warmup` | 30 | Seconds of steps recorded before the first write |
| `rate` | 50 | Change-and-restore pairs a second (1 to 1000) |
| `mode` | `write` | `noop`: compare-exchanges that change nothing; they still take the gate |
| `component`, `offset` | the automatic pick | Another component, and optionally its byte. The byte must be one of that component's padding bytes; any other byte stops the run before it writes |
| `label` | none | Free text for the analysis |

## The analysis

```sh
python3 analyze.py kiln-priority-kit-*.zip            # Markdown tables for the issue
python3 analyze.py kiln-priority-kit-*.zip --json     # the same as JSON
```

The CPU-load phase files sort every row into idle or loaded. Two seconds around each phase change are left out
(`--margin`). Each table has one row per platform (`windows`, `proton`), raise (`on`, `off`) and load (`idle`,
`loaded`):

- **Hold time:** every call that took the gate. The percentiles, the maximum, how many held 1 ms or more, and the
  busy share (calls that found the gate held by a frame), with a histogram from 10 µs to over 20 ms.
- **Server step from the mod's rows:** Pre to Pre is the step's interval. A spin at the start of a frame lengthens it,
  so "late" counts intervals over 1.5 times the idle median. Pre to Post is the step itself.
- **Server step from tick-stats.jsonl:** Kiln's own minute windows that lie inside one phase.
- **Outcomes**, and **runs**: what each run targeted and whether its byte is back.

What to look for: the raise matters if, under load, "off" shows long holds (milliseconds, a scheduler quantum) and
late steps while "on" does not. Windows and Proton differ if "on" under load looks like "off" on one of them.

## Building and testing the kit (maintainers)

```sh
KILN_OUT=<out> native/build.sh gate-priority-test world-ecs-fault          # the mod and the fault test, for Windows
KILN_OUT=<out> tools/priority-kit/package.sh                                # <out>/kiln-priority-kit.zip
KILN_OUT=<out> mods/tests/gate-priority-test/tests/run.sh                   # the mod over the fake engine (ASan/UBSan, TSan)
native/build.sh gate-priority-host
KILN_OUT=<out> WINEPREFIX=<private prefix> mods/tests/gate-priority-test/tests/wine.sh   # the mod's DLL under Wine
KILN_OUT=<out> tools/priority-kit/tests/run.sh                              # the scripts
```

- **The mod's host test** (`tests/kit_test.cpp`) runs the mod's own code against a fake Kiln whose kiln.world is the
  real compare-exchange code, over the fake engine of the world tests, laid out from the dedicated server's exact
  profile. kiln.types serves sigdb's reflection of that build. A simulated server thread runs frames that hold the
  Scene word and now and then change a real field of the target. One scenario per process: the run on time, the raise
  off, no-op mode, a named component and byte, a component without explained padding, a tracked one, no padding
  anywhere, another world, no world, no run file, a bad run file, and a world that stops mid-run. They check the
  target, every outcome, that every byte of every component ends as it was (the game's own changes kept), the raise
  (every hold above normal, or none), the files and their headers. Scenario `padding` checks the padding rule against
  an independent walk of sigdb's reflection of both exact builds: 687 components each, 201 with padding the
  reflection explains.
- **The Wine host** (`tests/host_win.cpp`) loads the real DLL and runs the same scenarios over kiln.engine's real
  Windows primitives: the SEH copies and writes, the Interlocked words, `SetThreadPriority`.
- **The scripts' tests** run `analyze.py` on made-up data, `collect.py` and `kit.sh` against a fake game folder, and
  `cpu-load.sh`'s schedule and its stop: no busy process survives Ctrl+C. Without `pwsh`, the PowerShell scripts are
  parsed by PowerShell's own parser from the PowerShell SDK (`tests/psparse`, restored from NuGet). They are also run
  once in an SDK runspace against a fake game folder. That is PowerShell 7 on Linux, with no registry and no CIM;
  Windows PowerShell 5.1 itself is not run there.
