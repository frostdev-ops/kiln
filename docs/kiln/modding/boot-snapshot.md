# The game-data snapshot for OnBoot

`Mod.OnBoot` runs before the game has loaded anything, so the game's items, templates, skill nodes and materials are
not in memory yet. What `boot.GameData.Read(...)` answers comes from a **snapshot**: a copy of those records, made from
the game's own files before the start, once per game build. This page is about the snapshot: who makes it, where it
lies, what it costs, and what a mod sees when it is not there.

## Who makes it

| Who | When | What you see |
|---|---|---|
| The Kiln launcher | at Play (and Launch in a server's modpack offer), right after the cook, when an enabled Kiln mod of the start has `"boot": true` or `"required"` in its kiln.mod.json | a progress line ("Game data for OnBoot mods (about 200 MB, once per game build): …"); a notice in one line only when something went wrong. Without such a mod: "no mod needs the boot snapshot", and nothing runs |
| `kiln boot-snapshot --game <dir> [--force] [--json]` | by hand, whatever the mods say: a server you start yourself, or to make it again | progress on stderr, then one line (or the JSON result) |
| `tools/deploy-server.sh` | at each deploy into a server folder, when an enabled Kiln mod in its profile or allowed drop-in folders has `"boot": true` or `"required"` (`KILN_NO_BOOT_SNAPSHOT=1` skips the step) | the tool's lines; "no mod needs the boot snapshot" without such a mod; a warning when it was not made |

All three run the same tool (`tools/bootsnap/bootsnap.py`, carried by the launcher and by `kiln`) with Kiln's Python:
the launcher's own on Windows (python.org's embeddable 3.14, in the launcher's folder), the system's `python3` on Linux
(3.10 or newer; one older than 3.14 needs the system's zstd library or the `zstandard` package), and for `kiln.exe` on
Windows `KILN_PYTHON`, a `python\python.exe` beside it, or `python` on `PATH`.

**The producer does not refuse a start.** If Python or the tool is missing, if the tool refuses or fails, or if it takes
longer than 10 minutes (the launcher stops it then), the launcher continues without a snapshot and says so in one line.
An OnBoot mod must still handle unavailable game data: an exception in its callback is subject to the boot phase's
startup failure policy.

The main Play action runs this step for the active profile, after refreshing that profile and cooking it. **Play once
with another profile and Launch in the modpack-offer dialog currently bypass this step.** Before either path, run
`kiln boot-snapshot --game <dir>` once for that game build. Launching through Steam also uses only a snapshot already
made; it does not run the producer.

## What it costs

**Only a game or server with an OnBoot mod pays it**: the launcher and the deploy step make the snapshot only when an
enabled Kiln mod has `"boot": true` (or `"required"`); a dedicated server does not count client-only mods. Mods of other
loaders (EML, SET, ShroudForge, Flame, ...) never need it. When no mod needs it, a snapshot already there is left as it
is (a later start with an OnBoot mod reuses it).

- **The first start after a game update**: half a minute to a minute (32 to 44 s measured on a busy PC, the client's
  and a dedicated server's data alike). A later start hashes the exe, reads the 64 KiB archive header, and verifies
  checksums of all eight snapshot files (about 200 MB of reads). Reuse time depends on storage speed; the earlier
  sub-second measurements predate checksum verification. A damaged snapshot is rebuilt. Older snapshots without
  producer checksums are rebuilt once, while the reader's format remains 1.
- **Disk**: about **200 MB** in the game folder (209 MB for game build 1287369: templates 158 MB, items 48 MB). One
  snapshot is kept: the one of the current game build. An older one is deleted only after the new one is written and
  reads back.
- It is written into the game folder you play (or serve) from; a client and a dedicated server each have their own.

## Where it lies

`<game folder>/kiln/data/boot-gamedata/<key>/`, where `<key>` names the game build: the first 32 hex digits of the
SHA-256 of the game exe's SHA-256 and the version string of the game's `.kfc`. kiln.boot computes the same key at boot
and opens only that folder. The key hashes the exe's 64 lower-case SHA-256 hex characters, one newline, then the archive
version bytes, and keeps the first 32 lower-case hex characters. The files (`index.json` written last, alongside one
index and one data file per kind) follow
the snapshot contract, format 1, which Kiln's boot service reads; a folder without a readable `index.json` is not a
snapshot.

The producer refuses symlinks and Windows junctions in the output tree below the selected game folder. Its `--root`
option must name that same output directory. A concurrent producer refuses immediately; a later start can retry.

## What it holds

Each value is the same camelCase JSON object kiln.content's live game-data reader gives for that record after the world
has loaded (member names as the game's reflection has them, GUIDs as text, enums and flags by name):

| Query | Entries |
|---|---|
| `item:<debugName>` | every `keen::ItemInfo` (`item:Material_T1_Wood_Logs`) |
| `template:<name>` | every template, by its name (or its GUID when it has none) |
| `skill:0x<8 hex>` | the nodes of the game's skill tree, by id (`skill:0x8efcc472`) |
| `material:<name>` | the rows of the game's building and terrain material table, named as the `GameMaterials` catalog names them (`material:StoneRough`, `Cell<id>` for a row no item places) |

It is **the game's data as it shipped**, read from the game's own archive: when EML (or another loader keeping EML's
backups) has patched the archive, the snapshot is made from the backup pair it kept (`.kfc.bak`,
`.kfc_resources.bak`). A record an EML mod patches therefore reads as the game's own in `OnBoot`, and as the patched one
after world load.

### Limits

- **40 template names occur twice** in the game's archive (game build 1287369). The snapshot keeps the first in the
  archive's order and lists the others under `skipped` in `index.json`. A mod that reads one of those names in
  `OnBoot` may get another template than the live registry gives after world load.
- **Materials** have no live reader to compare with: their rows are checked against the `GameMaterials` catalog rule.
- **Impact configuration names** (the keys under an `impactValues`-like member) come from the game's own impact
  programs. A config only a mod's program names reads as its `0x…` id in the snapshot.
- **An optional or variant member holding a single number** reads as `null`, as the live reader shows it.
- **A value larger than 4 MiB** is not stored (a `skipped` line says so).
- **Decoder budgets** also refuse oversized or malformed inputs before large allocations: 256 MiB executable/image,
  16 MiB archive directory or resource blob, 8 MiB decompressed chunk, 100,000 entries per kind and 100,000 JSON nodes
  per value. Each kind's data is limited to 1 GiB and each index to 32 MiB. A value exceeding its walk/byte budget is
  skipped with a reason; an archive exceeding its input budgets is refused. These bounds can require an update to
  the producer after a game format change.
- **A kind the game's archive cannot give** (a type the reflection lacks after a game update) is written empty: every
  read of that kind finds nothing. The launcher, `kiln boot-snapshot` and the deploy step each name such a kind in a
  warning.

## When there is no snapshot

On a start without a snapshot of the running game build (the tool could not run, it failed, or the game was started
from Steam, or with the launcher's "play once" with another profile, which makes none, before a Play made one),
`OnBoot` still runs, and:

- the capability row `mods.boot.game_data` is **unavailable**, with the reason "no game-data snapshot for this game
  build: start through the Kiln launcher, or run `kiln boot-snapshot --game <dir>`" and the test that failed (no
  folder of this key, an unreadable `index.json`, a kind file missing);
- `boot.GameData.Read(...)` throws `KilnUnavailableException("mods.boot.game_data")`.

A mod that can do without the game's data checks the row first (`Capabilities.Is("mods.boot.game_data")`;
`Capabilities.Get(...)` gives the reason) and keeps its own defaults. The next Play through the launcher makes the
snapshot (or run the command above), and from then on the reads work.

## The command

```
kiln boot-snapshot --game "C:\Program Files (x86)\Steam\steamapps\common\EnshroudedServer"
kiln boot-snapshot --game ~/srv/enshrouded --force --json
```

Exit 0 when a snapshot of this game build is in place (made now, or current), also when a kind is empty (a warning
names it); 1 when there is none (the reason is printed). `--json` prints the result: `{status, key, path, counts,
emptyKinds, skipped, archive, bytes, seconds, reason}`, `status` one of `made`, `reused`, `refused`, `failed`,
`unavailable` (the launcher and the deploy step also give `skipped`: no mod needs it).

From a Kiln checkout the tool also runs by itself: `python3 -I tools/bootsnap/bootsnap.py make --game <dir>` (and
`show --game <dir> item:<name> …` prints entries of the snapshot there).
