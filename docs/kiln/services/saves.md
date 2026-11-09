# kiln.saves: save events and persistent mod data

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_saves.h` (`kiln.saves` v1) |
| Native | `native/services/saves/` -> `kiln/services/kiln-saves.dll` |
| Symbols | `sigdb/symbols/server.d/saves.json` (features `saves.request`, `saves.write`, `saves.load`, `saves.reasons`) |
| Managed | `managed/services/Kiln.Saves/` -> `kiln/host/Kiln.Saves.dll` (`Saves`, `WorldData`, `PlayerData`, `CharacterData`, `EntityData`, events) |
| Test mod | `mods/tests/saves-test/` (five boots: SIGINT, 3x kill -9, restore) |
| Evidence | `docs/re/saves.md` |

Forge equivalents: `LevelEvent.Save` → `WorldSavingEvent`, `LevelEvent.Load` → `WorldLoadedEvent`, `SavedData` →
`WorldData`, player persistent data → `PlayerData`, capabilities / data attachments → `EntityData`.

## Events (the event bus, namespace `Kiln`)

| Event | When | Thread | Cancel | Fields |
|---|---|---|---|---|
| `WorldSavingEvent` | the game accepted a world save (autosave every 30 s, a player left, `Saves.Request`, `Saves.Backup`); before the game snapshots the world. For the shutdown save: right after `ServerStoppingEvent`, before mods unload | tick thread; normally inside the game's save request after Kiln waited for the world's jobs (= Post rules), `BeforeSnapshot` true | no | `Reason`, `Revision` (the world file's revision once written), `WorldId`, `WorldFile`, `BeforeSnapshot`, `Step` |
| `WorldSavedEvent` | the world file is complete (or the write failed / a Kiln request timed out) | tick thread, Pre of the next step | no | `Reason`, `Success`, `Revision`, `Duration`, `Result`, `TimedOut`, `WorldId`, `WorldFile` |
| `WorldLoadedEvent` | the server finished loading the world save | tick thread, Pre | no | `WorldId`, `Revision`, `NewWorld`, `Data` (`DataMatch`), `WorldFile` |

All fields are read-only. Data written to `WorldData`/`PlayerData`/`EntityData` in a `WorldSavingEvent` handler is part
of that save's generation. Kiln.Saves starts by itself when a mod subscribes to one of these events or touches any of
its classes (module initializer).

## Persistent data

```csharp
using Kiln;

public sealed class Bank : Mod
{
    public sealed class Ledger { public Dictionary<ulong, long> Coins { get; set; } = new(); public int Day; }
    public sealed class Wallet { public long Coins { get; set; } }

    public override void OnLoad()
    {
        var ledger = WorldData.Get<Ledger>("ledger");          // live object: change it in place, Kiln saves it
        Log.Info($"day {ledger.Day}, data {WorldData.Loaded}");   // Exact / Staged / Older / None
    }

    [Subscribe]
    private void OnSaving(WorldSavingEvent e)                    // runs before the game's snapshot
    {
        WorldData.Get<Ledger>("ledger").Day++;                   // lands in revision e.Revision
    }

    public void Pay(ulong steamId, long n) => PlayerData.Get<Wallet>(steamId, "wallet").Coins += n;

    public void MarkChest(EntityId chest, string owner) =>
        EntityData.Set(chest, "owner", new Owner { Name = owner });   // follows the chest across restarts
}
```

| API | Notes |
|---|---|
| `WorldData.Get<T>(key)` / `TryGet` / `Set` / `Remove` / `Contains` / `Keys` / `Loaded` | per world, per calling mod. `Get` creates `new T()`. JSON via System.Text.Json (public properties and fields) |
| `PlayerData.Get<T>(steamId, key)` / `TryGet` / `Set` / `Remove` / `Contains` / `Keys(steamId)` / `Players` | per world, per Steam id, per mod; the player need not be online. All characters of the account share it: for character progress use `CharacterData` |
| `CharacterData.Get<T>(key or entity, k)`, `CharacterData.Carried.*` | per character, per mod: this world's record of the character, and what goes with it to other worlds. [character-data.md](character-data.md) |
| `EntityData.Get<T>(entity, key)` / `TryGet` / `Set` / `Remove` / `Has` / `Clear` / `IsPersistent` / `Entities` / `Waiting` / `Available` | lives as long as the entity (destroyed: dropped within 1 s); known, saved world templates with a position get it back after a restart. Inventory item entities are session-only. Player entities map to `PlayerData` (Steam id) |

Rules: tick thread, from mod code (handlers, `OnLoad`, tasks; in a hosted world, once `Saves.WorldDataReady`: see "In a game client"). Keys belong to the calling mod (`kiln.mod.json` id).
A stored value that no longer reads as `T` throws `InvalidDataException` and is kept as it was. A mod reload keeps the
data (objects are serialized when the mod unloads and read into the new instance's types).

### How consistency works
- The world file's header revision (+1 per successful write) names the generation. When the game accepts a save,
  Kiln runs the `WorldSavingEvent` handlers, serializes every mod's data and writes generation *R+1*
  (`manifests/<R+1>.pending.json`), then the game writes the world file as revision *R+1*; after the write Kiln
  confirms it (`manifests/<R+1>-<file time>.json`).
- With `Saves.SnapshotStagingAvailable`, Kiln serializes that generation once more at the first safe Post after the request: simulation jobs have finished, and the game's pending snapshot has not run yet. A request after Post waits until the following tick's Post. This catches inventory commits made by that last simulation. `WorldSavingEvent` handlers run once; JSON serialization callbacks can run twice. Later Posts during the background write do not replace the staged generation. Shutdown without another simulation keeps the initial stage.
- This ordering covers native inventory transactions, including container default loot. It does not promise that an arbitrary mod's writes in a later Post callback are captured; make save data changes before the final staging callback.
- Loading picks the generation of the world file on disk (revision + file time); a crash or `kill -9` at any moment
  gives the old or the new generation, matching the world file, never a mix: every file is written to a temp name,
  flushed and renamed, and bundles are content-addressed (`blobs/ab/<sha256>.json`, checked on load).
- Restoring an older world file brings back its mod data: Kiln keeps the newest 30 generations and every generation of
  a world file in the save folder or its `backup/` (the game's own 10 backups). An older restore (no generation kept)
  loads the newest older generation (`DataMatch.Older`, logged as a warning).
- Entities: attachments are stored with the entity's template and position at the save and given back to the loaded
  entity of that template there (props within 0.5 m; creatures and NPCs within 16 m, nearest unclaimed), retried for
  10 minutes after a load (entities stream in), kept for 3 sessions if never matched. This needs a known, saved
  template with `CurrentTransform`. DoNotSave templates (most enemies, loot pickups), inventory item entities,
  and unknown templates: session only. An inventory item's default position is not a persistent identity;
  old item attachments are never matched to a different sword after a restart.

## Saves

| Member | Notes |
|---|---|
| `Saves.Available`, `Status` (`SaveStatus`: state, last save, revision, autosave interval, counters, data generation) | any thread |
| `Saves.WorldId`, `WorldFile`, `SaveDirectory`, `DataDirectory`, `BackupDirectory` | the world's GUID text (from the world file header) and paths |
| `await Saves.Request()` → `SaveResult` | the game's own save now (the one its timer makes); completes when a save that started after the call is written. Refused while the world does not run; dropped after 60 s |
| `await Saves.Backup(name)` → `BackupInfo` | saves now, then copies the world file + its mod-data generation to `kiln/backups/<UTC stamp>-<name>/` (`backup.json` with sha256 per file) on the tick thread right after the write, before the game can start another save |
| `Saves.Backups` | `BackupInfo` list |
| `Saves.RestoreOnNextStart(name)` / `CancelRestore()` / `PendingRestore` | stages `kiln/backups/restore.json`; at the next server start kiln.saves checks the backup's world file header, copies the files it replaces to `kiln/backups/_before-restore-<stamp>/`, puts the world file and its sidecar generation in place before the game opens its save, and writes `restore.result-<stamp>.json` |

`kilnctl backup <name>` archives the save folder and then `kiln/data` (sidecars of kiln.saves and kiln.spawn, mods'
data folders) in that order, so the archive always holds the mod data of the world file in it. Restore with the server
stopped: `tar -xzf <backup> -C <dir>`.

## Storage

```
kiln/data/worlds/<world id>/
  manifests/0000003010.pending.json        generation staged for revision 3010 (before the world write)
  manifests/0000003010-1793770110.json     ... confirmed after the write (file time 1793770110)
  blobs/9f/9f3c...e1.json                  one bundle: world/<mod>, player/<steam id>/<mod>, character/<key>/<mod>, carried/<key>/<mod> or entity/<mod>
kiln/backups/<stamp>-<name>/backup.json, savegame/world0.ksd, worlds/<id>/{manifests,blobs}
```
`kiln/data/worlds` is reserved (a mod with id `worlds` would clash with it).

## C (native plugins)

```c
#include "services/kiln_saves.h"
static const KilnSavesApi* saves;
/* on_ready */ saves = (const KilnSavesApi*)api->service((KilnStr){KILN_SAVES_SERVICE, 10}, 1);
KilnSavesStatus st = {sizeof st}; saves->status(&st);          /* any thread */
saves->request(KILN_SAVE_REASON_REQUEST);                       /* any thread; at the next step */
KilnSaveEvent ev; uint64_t seq = 0;
while (saves->next_event(seq, &ev) == KILN_OK) { seq = ev.seq; /* SAVING / WRITTEN / FAILED / LOADED */ }
```
The pump (`set_pump`) is taken by Kiln.Saves; native plugins poll `next_event` (a ring of 256).

## Limits
- One world per server (the dedicated server's `saveDirectory/world0.ksd`). A brand-new world has no id until its
  first save; data written before that is kept in memory and written with the first save.
- The kill -9 window: none for the generation choice. A generation is staged before the world write; a crash between
  the two leaves revision R on disk and loads generation R.
- Moving creatures may be matched to the wrong entity of the same template if two stand within 16 m of the saved spot.
- System.Text.Json caches the metadata of every type it serialized process-wide, which would keep an unloaded mod's
  load context alive; Kiln.Saves resets those caches (the runtime's hot-reload hook) when a mod whose data it
  serialized unloads.

## In a game client (hosted worlds)
- World files: a slot id is `(storage << 8) | (index + 1)`, storage 0 Local, 1 Steam. Steam:
  `<Steam>\userdata\<account id>\1203620\remote\world<index>.ksd` (found through `STEAM_COMPAT_CLIENT_INSTALL_PATH` under
  Proton, `SteamPath` in the registry on Windows; of several accounts the one whose file changed last). Local:
  `<Saved Games>\keen games\Enshrouded\default\world<index>.ksd`. `SAVE_DIR` is the hosted world's folder.
- kiln.saves only **reads** these files. Staged restores are refused on a client (`KILN_SAVES_CLIENT_HOSTED` in
  `status.features`; `Saves.RestoreOnNextStart` throws NotSupportedException, a `restore.json` left there is renamed to
  `restore.refused-<time>.json`): never write into a client's Steam cloud or local save folders.
- Each hosted game is a session: after the game's shutdown save and the world's close, the state returns to NONE and the
  next world's load is reported again (`LOADED`, with its own GUID, slot and revision).
- **Mod data goes with the world.** A hosted world's data closes with it, and the next world's opens at the game's load (`WorldLoadedEvent`).
  The next world starts (`ServerStartedEvent`, server-side mods' `OnLoad`) about 10 s before that: in between `WorldData`, `PlayerData`,
  `CharacterData` and `EntityData` are empty, `Saves.WorldDataReady` is false, and what is written there goes (a new world, with no file yet, keeps it).
  Every world of a process starts as the first does. So decide nothing from "this key is not there" before `Saves.WorldDataReady`: `WorldLoadedEvent` is the moment. Before that fix the
  closed world's data stayed open: a later world read it in those 10 s (the Warden "was placed once in this world and is gone" while he stood there), and a new world filed its
  data under the old world's id. A save's late write still commits: what it reports after the close stays staged, and the load finds it. On a build without the game's load hook the data stays open, as before, with a warning.
  Lab: `mods/tests/world-switch-test` plays the order with `Saves.LabCloseWorld` and `Saves.LabWorldLoaded`.
