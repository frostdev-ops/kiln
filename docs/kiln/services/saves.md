# kiln.saves: save events and persistent mod data

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_saves.h` (`kiln.saves` v1) |
| Native | `native/services/saves/` -> `kiln/services/kiln-saves.dll` |
| Symbols | `sigdb/symbols/server.d/saves.json` (features `saves.request`, `saves.write`, `saves.load`, `saves.reasons`) |
| Managed | `managed/services/Kiln.Saves/` -> `kiln/host/Kiln.Saves.dll` (`Saves`, `WorldData`, `PlayerData`, `CharacterData`, `EntityData`, `InstanceData`, events) |
| Test mod | `mods/tests/saves-test/` (five boots: SIGINT, 3x kill -9, restore) |
| Unit tests | `managed/tests/Kiln.Saves.Tests/` (instance data: `InstanceMatcherTests`, `InstanceMatcherPropertyTests`, `InstanceStoreTests`, `InstanceApiTests`) |
| Evidence | `docs/re/saves.md` |

Forge equivalents: `LevelEvent.Save` → `WorldSavingEvent`, `LevelEvent.Load` → `WorldLoadedEvent`, `SavedData` →
`WorldData`, player persistent data → `PlayerData`, capabilities / data attachments → `EntityData`.

## Events (the event bus, namespace `Kiln`)

| Event | When | Thread | Cancel | Fields |
|---|---|---|---|---|
| `WorldSavingEvent` | the game accepted a world save (autosave every 30 s, a player left, `Saves.Request`, `Saves.Backup`); before the game snapshots the world. For the shutdown save: right after `ServerStoppingEvent`, before mods unload | tick thread; normally inside the game's save request after Kiln waited for the world's jobs (= Post rules), `BeforeSnapshot` true | no | `Reason`, `Revision` (the world file's revision once written), `WorldId`, `WorldFile`, `BeforeSnapshot`, `Step` |
| `WorldSavedEvent` | the world file is complete (or the write failed / a Kiln request timed out) | tick thread, Pre of the next step | no | `Reason`, `Success`, `Revision`, `Duration`, `Result`, `TimedOut`, `WorldId`, `WorldFile` |
| `WorldLoadedEvent` | the server finished loading the world save | tick thread, Pre | no | `WorldId`, `Revision`, `NewWorld`, `Data` (`DataMatch`), `WorldFile` |
| `InstanceDataRestoredEvent` | a saved record found its item after a load or a join (one per key) | tick thread | no | `Item`, `Mod`, `Key`, `Match` (`Exact`, `Moved`), `Detail` |
| `InstanceDataParkedEvent` | a record waits: its holder was not found this session, the player left with the item, or no item of the holder fits it (one per mod) | tick thread | no | `Holder`, `Character`, `FlatSlot`, `Item`, `Mod`, `Keys`, `Reason` (`HolderAway`, `PlayerLeft`, `NoCandidate`), `Detail` |
| `InstanceDataLostEvent` | a record no longer goes with an item: destroyed, not found in 3 sessions, over the cap, or set aside because it was not certain (one per mod) | tick thread | no | `Item`, `LastRef`, `Mod`, `Keys`, `Reason` (`Destroyed`, `Unmatched`, `Ambiguous`, `Capped`), `Detail` |

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
| `EntityData.Get<T>(entity, key)` / `TryGet` / `Set` / `Remove` / `Has` / `Clear` / `IsPersistent` / `Entities` / `Waiting` / `Available` | lives as long as the entity (destroyed: dropped within 1 s); known, saved world templates with a position get it back after a restart. Inventory item entities are session-only here (`IsPersistent` is false for them): for data on one item use `InstanceData`. Player entities map to `PlayerData` (Steam id) |
| `InstanceData.Key<T>(name, options)` → `InstanceKey<T>` | declares a key of the calling mod, once (a static field of the mod's class, or `OnLoad`); the same name again returns the same key, another type or other options throw `ArgumentException`. `InstanceKeyOptions { Persist = World / Carried / Session, Items }`. Declaring needs no capability. [Instance data](#instance-data-values-that-stay-with-one-item) |
| `item.Get(key)` / `TryGet(key, out v)` / `Has(key)` / `Set(key, v)` / `Remove(key)` on an `ItemInstanceRef` | the value on that one item; `Get` gives a live object (create with `new T()`), saved with the world. `[ServerOnly]`, `[TickThreadOnly]` |
| `InstanceData.All(key)` / `Parked(key)` | every item of this session with the key, with its value; the saved records that are not on an item now (`Waiting` or `SetAside`, with the reason; the values are copies) |

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
- Instance data rides the same generation: at both stagings (`WorldSavingEvent` and the snapshot staging) the place and
  record fields of every item with data are read fresh from the game and written as bundles `instances/<mod>` and
  `character-instances/<character>/<mod>`, so a crash gives the old or the new generation of them too.
- Entities: attachments are stored with the entity's template and position at the save and given back to the loaded
  entity of that template there (props within 0.5 m; creatures and NPCs within 16 m, nearest unclaimed), retried for
  10 minutes after a load (entities stream in), kept for 3 sessions if never matched. This needs a known, saved
  template with `CurrentTransform`. DoNotSave templates (most enemies, loot pickups), inventory item entities,
  and unknown templates: session only. An inventory item's default position is not a persistent identity;
  old item attachments are never matched to a different sword after a restart.

## Instance data: values that stay with one item

```csharp
using Kiln;

public sealed class Kills { public int Count { get; set; } }

public sealed class Swords : Mod
{
    private static readonly InstanceKey<Kills> KillsKey = InstanceData.Key<Kills>("kills");

    // sword: an ItemInstanceRef (the item entity and its stamp) from Items.Held(player), an inventory read's
    // Slots[i].Ref, or a give, take or move result
    public void CountKill(ItemInstanceRef sword) => sword.Get(KillsKey).Count++;   // follows this sword, not the slot

    public int KillsOf(ItemInstanceRef sword) => sword.TryGet(KillsKey, out var k) ? k.Count : 0;

    [Subscribe]
    private void OnLost(InstanceDataLostEvent e) => Log.Info($"{e.Mod} {string.Join(",", e.Keys)} on {e.Item}: {e.Reason} ({e.Detail})");
}
```

**Status in this release:** the store, the matching and the API are proven by unit tests against a fake of the game's
items (`managed/tests/Kiln.Saves.Tests`), and read the game through kiln.items' item instances (kiln.items v4:
stamps, locations, save-record fields, the tracker; `InstanceGame.cs`). On a server whose kiln.items has no v4, or has
the feature off, every call but `Key` throws `KilnUnavailableException("saves.instance_data")` naming `items.instances`
in its reason. Lab results follow.

| Capability | Means |
|---|---|
| `saves.instance_data` | `InstanceData` works: kiln.saves, kiln.items' item instances (`items.instances`), and the world's data open (`Saves.WorldDataReady`) |
| `saves.instance_data.carried` | `Persist.Carried`: not available in this release; declaring such a key throws `KilnUnavailableException` |

**`Persist.Carried` is not available yet.** `Persist.World` keeps the value on this world's save, the item in a player's
inventory included; data that travels with a character between worlds is not available yet. Declaring a key with
`Persist.Carried` throws `KilnUnavailableException("saves.instance_data.carried")` with that text as its reason.

Errors follow [the contract](../modding/contract.md): a none ref, a ref without a stamp, a stale ref (the item is gone,
or its id belongs to another item now), an item type the key does not allow, a value over 64 KB as JSON at `Set`:
`ArgumentException`; `TryGet`, `Has` and `Remove` answer false for a stale ref. A stored value that does not read as
`T`: `InvalidDataException`, kept as stored. Not the tick thread or not mod code: `InvalidOperationException`; a key whose
mod instance unloaded: `InvalidOperationException` (declare it again).

### Where it is kept
| The item is in | Kept in | Scope |
|---|---|---|
| a chest, tombstone, station, NPC or animal | the world's generation | `instances/<mod>` |
| a player's inventory (`Persist.World`) | this world's record of the character | `character-instances/<character key>/<mod>` (not listed by `CharacterData.Keys`) |
| anywhere, `Persist.Session` | nowhere: this server session only | |

One record per item: an item that moves from a chest to a player is written in the character's scope at the next save
and is gone from the chest's; nothing is copied. A holder without a persistent identity (not saved with a position, or
a player whose character has no id on this build) keeps its items' data for the session only, logged once; so does an
item entity that sits in no holder's slot at the save.

**Slot numbers.** A record's `slot` is kiln.items' flat slot, kept as an opaque number unique in its holder. 0 to 255
are the numbers the game's own world and character saves use (Generic links only: 8 per Generic link before this one,
plus the index). 256 and up are slots the game's save does not number that way (equipment, currency, customization,
nested links: 256 + the link's position in the holder's link tree x 8 + index). Such a number means the same slot after
a rejoin only if the player's link tree comes back the same; that is not proven without a player.

### Bundle format (`format` 1)
```json
{ "format": 1, "scope": "instances/<mod>",
  "holders": [ {
    "holder": { "kind": "world", "template": "<guid>", "pos": [x, y, z], "mobile": false, "name": "<template name>" },
             // or { "kind": "character", "character": "<account hex>.<character id>" }
    "missed": 1,              // sessions the holder was not found (omitted: 0)
    "crowded": true,          // another entity of its template stood within the radius at the save (omitted: false)
    "aside": "<reason>",      // the holder itself was not certain: all its records are set aside
    "last": 3011,             // a player: the newest witness taken while in game
    "joins": 1,               // a player: joins counted (a join's witness id is 2147483648 + this count)
    "witness": { "3011": { "settled": true,   // read at the snapshot staging or a shutdown save (see below)
                           "items": [ [flat slot, item, count, level, dur, durMax, fuel, fuelMax, dye, perksAvail, perksUnlocked, gem, gemLevel], ... ] } },
    "records": [ {
      "id": "<16 hex>",       // joins the parts of one item in several mods' bundles
      "slot": 3, "rec": [item, count, level, dur, durMax, fuel, fuelMax, dye, perksAvail, perksUnlocked, gem, gemLevel],
      "seen": 3011,           // the save whose fresh read placed it (the key of its witness)
      "state": "live",        // live (on its item at the save), waiting, aside
      "why": "<reason>", "missed": 0, "since": 3011, "gone": true,
      "alt": [ [2147483649, 10], [3010, 11] ],   // a player's: its slot at the other states the client may come back as (-1 not there, -2 not read)
      "keys": { "<key>": <the value as JSON> } } ] } ] }
```
A witness is the holder's whole list of item entities at that save (also those without data). Each mod's bundle carries
the holders and records of that mod; a reader joins records with the same id, slot, fields and `seen`. A witness written
as a bare list (an earlier writer of format 1) reads as not settled.

### How the matching decides
The game keeps no id for an item. A saved record is a holder, a flat slot (see "Slot numbers" above), an
item id and the fingerprint of the save record: a stable part (item id, level, perks available and unlocked, gem item
and level, dye) and a volatile part (durability and fuel, compared as ratios within 0.002, because a load rebuilds them
from ratios). After a load or a join, per holder:

Every record needs its **witness**, the holder's list at the save that placed it. A record without one (saved before
any save could read its holder, for example an item a player got and left with before the next autosave) is set aside:
without it nobody knows whether a look-alike stood beside it.

1. **The holder is as it was saved, and as the loader made it.** Three things must hold, or rule 1 does not apply:
   - *between the save and the load:* the holder's list now equals the record's witness, every slot and every field;
     and for a world holder also the record was read at the save of the generation the world file vouches for (`Exact`
     or `Staged`). Both, because a world file edited offline with its header revision and time kept loads as `Exact`;
     its changed list shows. For a player the witness alone (a character has no generation of its own), and see
     "Players" below: every state the character may come back as is compared;
   - *at the save:* that witness is **settled**: read at the snapshot staging, where no simulation step comes before the
     game's snapshot, or at a shutdown save. The first staging of a save is one step early, and a swap in that step is
     saved by the game but not by Kiln;
   - *between the load and the match:* the holder's items are as the loader made them in this session. Entity ids come
     from one counter that only grows, and the loader makes a holder's items in one go, so the ids must ascend in
     flat-slot order (no two items changed places), form one run right after the holder's own id (nothing made later or
     brought in from elsewhere), and, when the generation's witness tells the loader's slots, fill exactly those slots
     (nothing moved or taken out). Whether the loader really works this way is measured in the lab; until then this check
     stands as written, and if it does not hold there, rule 1 stays off.

   Then a record goes to the item in its own slot if item id and stable fingerprint agree, twins included. Otherwise it
   waits.
2. **Otherwise** (an older generation, a character that comes back changed, a list that differs, an unsettled witness,
   items that changed since the load): a record goes to an item only if that item is the one item of the holder with
   its item id and stable fingerprint, no other record of the holder has that fingerprint, and its witness shows no
   look-alike of it at the save either. In its own slot that is `Exact`, elsewhere `Moved`.
3. **Not certain** (several candidates, several records, a look-alike gone since the save, no witness, an item that
   already has data of this session): every such record is set aside and reported (`InstanceDataLostEvent`,
   `Ambiguous`). It is kept, listed by `Parked` and `/instances`, and never attached automatically, also not in a later
   session.
4. **No candidate:** the record waits (`InstanceDataParkedEvent`, `NoCandidate`); after 3 sessions without its item it
   is dropped (`Unmatched`).
5. **The holder:** a world holder is the one unclaimed entity of its template within 0.5 m (props) or 16 m (creatures,
   NPCs) of the saved position; two or more, or another one within the radius at the save: its records are set aside.
   Holders not found yet are looked for during the whole session, 16 per tick (about a second) in turn, and at once when
   a mod touches an item in one. So a chest that streams in late is matched within a pass (200 waiting holders: about
   13 s), before players move its items about. A holder not found by the end of the first 10 minutes after a load counts
   as missed for that session; found later, that is undone. A player is found by `CharacterKey` once the inventory is
   initialized.

A record read at an older save but carried into a newer generation unread (its holder was away) is not vouched for by
that generation: it is matched by rules 2 to 4, or by its own witness.

**A build without snapshot staging** (`Saves.SnapshotStagingAvailable` false; kiln.log says so once at start): no
witness is settled except a shutdown save's, so identical items (twins) cannot be told apart after a restart there. They
are set aside; an item without a look-alike still finds its record.

**What nobody can detect:** two items with equal fingerprints swapped while no Kiln was watching, with nothing else in
the holder changed; and an item removed while no Kiln was watching with a look-alike put in its place. Both look exactly
like nothing happened.

### Gone items, players, mods
- **Destroyed** (trash, crafting, taken away) in a world holder: dropped at once (`Destroyed`).
- **Gone from a player's inventory:** not decided in that step. If the player is still in game 50 steps later, or the
  inventory was still there when the item went, it was destroyed. If the player left, the record waits with the
  character (`PlayerLeft`) and comes back at the next join. A save in between keeps such a record, but it may only come
  back to an inventory that is as one of its saved states.
- **A read that places an item nowhere** (for one step at a leave the item entities outlive the player entity) changes
  nothing: the item keeps its last place, its holder and its newest observation.
- **A leaving player** keeps its character: while an item stays in the same holder entity, a later lookup that finds no
  character (a record already reset) never replaces the one known.

### Players: which file comes back
A player leaves in one of three ways (`out/instances/t3/leave-order.md` of the dev.25 wave): from the menu (a leave save
while the player still counts as in game, its file sent and acknowledged, then the entities go), by a crash or a lost
connection (no leave save: the client keeps the file of the last save it received, up to 30 s old, or older if sends
failed), or with the server's shutdown. So a character may come back as any state the client got: the inventory it
joined with, or the inventory at a later save. Kiln reads every player's inventory at its join and at every save (with
data or not) and keeps, for the newest 5 of these states of the visit (the join and about two minutes of autosaves), where
each item was. A record carries its slot in each. At the next join the inventory is compared with all of them: the
states it equals must agree on the item's slot (then it attaches there, also from an older state), or all lack it (then
it waits). States that disagree, which happens when look-alikes were moved within those two minutes, set the record
aside. A save that could not read a player's inventory counts as a state nobody knows: a record with it is set aside.
What this rests on: the client's file is one of the newest 5 states (a connection dead for longer is timed out by the
server), and the client's file changes only when the server sends the character back, which it does at its world saves.
- **A mod that is not loaded:** its records are kept as JSON, unread, matched and moved with their item like any other,
  and dropped only with the item.
- **A mod's unload:** its live objects are written out as JSON and every reference to its types goes (keys included);
  a reload of the same mod id declares its keys again and reads the same data.
- **Hosted worlds:** the instance data closes and opens with the world, as the rest of the mod data.

### Caps and cleanup
- At most 256 records per holder per mod (on an item, waiting and set aside together). Over that, the oldest waiting or
  set-aside records go first (`Capped`).
- 64 KB per value as JSON, checked at `Set` (`ArgumentException`). A live object that grows past it is not written in
  that state: its last form that fit is saved, logged once per item and key, shown by `/instances`.
- A holder never found: kept 3 sessions, then dropped (`Unmatched`). A session without kiln.items' item instances does not
  count, and its records ride into the next generation unchanged.

### `/instances [player|entity]`
Admin only, owned by "kiln" (node `kiln.command.instances`). Per mod: what is attached (item, holder, slot, keys), what
waits and what is set aside (holder, slot, item, reason, sessions missed), and values that did not fit. A player name
or an entity id (a holder or an item) narrows it. Kiln.Saves registers it late-bound, by name, when Kiln.Chat is loaded
(the first key's declaration, or the first service tick); a mod that only stores data does not load the chat library.

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
  blobs/9f/9f3c...e1.json                  one bundle: world/<mod>, player/<steam id>/<mod>, character/<key>/<mod>, carried/<key>/<mod>,
                                           entity/<mod>, instances/<mod> or character-instances/<key>/<mod>
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
  Lab: `mods/tests/world-switch-test` plays the order with `Saves.LabCloseWorld` and `Saves.LabWorldLoaded` (lab members:
  `[Experimental("KILNX001")]` and hidden; a lab mod puts `KILNX001` in its `NoWarn`).
