# kiln.assets: mod resources, overrides, icons and retextures

Give a mod its own game resources and let it override vanilla ones at run time — item icons, retextures, and (with the
models track) meshes — the way a resource pack does. **No game file on disk is changed.** kiln.assets answers the
game's resource service (key → handle → data) and its content reader (content hash → bytes) from memory, so a mod's
resources live only while the mod is loaded and come off cleanly. Evidence and the RE: `docs/re/resources.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/assets.json` (features `assets.resources`, `assets.content`); client overlay `client.d/assets.json` (`assets.ui_icons`) |
| C table | `native/core/abi/services/kiln_assets.h` (`kiln.assets` v1) |
| Native | `native/services/assets/` → `kiln/services/kiln-assets.dll` (embeds stb_image + bc7enc_rdo, `native/third_party/{stb,bc7enc}`) |
| Managed | `managed/services/Kiln.Assets/` → `kiln/host/Kiln.Assets.dll` (namespace `Kiln`, class `Assets`) |
| Tool | `tools/assets/make-sample-pngs.py` (the sample art); `tools/assets/render-icon.py` (an item icon rendered from a glTF); `tools/kfc` (models worker's reader) lists resource GUIDs and formats |
| Sample | `mods/samples/AssetSample/` (a new item's PNG icon + a retexture of a vanilla UI image) |
| Test | `mods/tests/assets-test/` (server-side PASS/FAIL: resources, blobs, overrides, dump) |

## What a mod can do
- **Icon** for an item: a PNG shown in the inventory and crafting UI, keyed by the item's GUID (client). A new item gets
  a new icon texture; a game item (a model override that needs a matching icon) has its own icon texture retextured
  in that texture's format, size and mips (see "Icons for model overrides").
- **Retexture**: replace a vanilla texture's pixels with a PNG, re-encoded to the texture's own BCn/RGBA format (client).
- **Resource**: supply a new resource, or override a vanilla one's bytes, for any resource type (both sides where the
  game loads the type).
- **Content blob**: supply new blob bytes, or override a vanilla blob, that a resource references (both sides).
- **Dump**: write a resource's or blob's bytes to `kiln/data/assets/dump/` (for tooling, e.g. the models cooker).

Sides: icons and textures are **client only** (`[ClientOnly]`; a dedicated server never draws; the calls throw
`KilnUnavailableException` there, naming `assets.ui_icons` or `assets.textures`; it is a `NotSupportedException`, as
before). Each member carries `[RequiresCapability]` with the row it needs: `kiln.assets`, `assets.resources`,
`assets.content`, `assets.ui_icons` or `assets.textures` ([caps.md](caps.md)); every member is `[AnyThread]`.
Resources and content blobs work on **both** sides — the server loads templates and
item data too. The server's game data has no `keen::RenderModel`, so meshes only show on the client.

## Mod layout: `assets/assets.json`
A mod folder may hold `assets/assets.json`, loaded natively at boot (before the game's UI builds) for every mod in the
load order, and also loadable from code with `Assets.LoadManifest(dir)`. Paths are relative to the `assets/` folder;
subfolders are allowed; absolute paths and `..` are rejected.

```json
{
  "format": 1,
  "entries": [
    { "kind": "icon", "guid": "<item GUID>", "file": "icons/my_item.png" },
    { "kind": "texture", "guid": "<UiTextureResource GUID>", "type": "keen::UiTextureResource", "part": 0,
      "file": "retex/grenade.png" },
    { "kind": "resource", "guid": "<GUID>", "type": "keen::RenderModel", "part": 0, "mode": "override",
      "file": "cooked/sword.bin", "patch": [ { "offset": 272, "blob": "cooked/sword.blob" } ] },
    { "kind": "blob", "file": "cooked/sword.blob" },
    { "kind": "blob", "hash": "<decimal size>-<h0>-<h1>-<h2>", "file": "retex/pixels.bin" }
  ]
}
```

- **`kind`**: `icon` | `texture` | `resource` | `blob`. On a dedicated server the entries only a game client uses are
  skipped by what they are, whether or not their files are there: `icon` and `texture` entries, a `resource` of a type a
  server's files never hold (`keen::RenderModel`, `keen::RenderMaterialResource`, `keen::RenderTextureResource`,
  `keen::UiTextureResource`), and a `blob` that only such resources name in `patch` (a model's render data; a blob with
  a fixed `hash` is always kept: it replaces game content a server mod may read by that hash). The report
  counts them (`… (4 entries for clients only, skipped on this server) …`). So the manifest is the same file on a server
  and in a player's game: a server's cook makes no models, and nothing has to be marked for it (dev.27; the cook of
  0.2.0-dev.26 wrote `"side": "client"` on the models' entries of the server's copy, and the server now names such a
  copy in a warning: it is another file than the players').
- **Every file is read from the mod's own `assets` folder** (`contained.cpp`, its portable half `scan.cpp` with the host
  test `tests/test_scan.cpp`): a link or junction at the `assets` folder itself or below it (by name, which shows Wine's
  links to folders; on Windows the final paths too: the assets folder's inside the mod folder's, the opened file's
  inside the assets folder's) makes the entry an error, as does a file whose identity (volume, file index) is that of a
  cooked file of any mod of the load order while its name is not under the mod's own `assets/cooked` (a Unix link to a
  file is a plain file under Wine). The cooked folders of every enabled mod of the load order, with an `assets.json` or
  not, are scanned whole (an explicit stack, no link followed, at most 200000 entries each); a scan that is cut is named in kiln.log, and then a file outside its own `assets/cooked` is
  refused under `"local"` and named under `"warn"`. The bytes come from the handle that was checked.
- **A file in `assets/cooked`** (a `patch` blob too) is compared with the cook receipts of this game
  (`<game>/kiln/state/cook-receipts/*.json`, written by the cook tools when Kiln runs them:
  [../modding/cooking.md](../modding/cooking.md)). One no receipt lists is handled by `kiln.json` `"cooked"`: `"warn"`
  (the default, `kCookedDefault`) loads it and says once per mod which files and how to cook them (`…, 2 cooked files
  not made here (loaded)`), `"local"` leaves the entry out (a resource whose patch blob is not listed, whole: `…, 2
  cooked files not made here`), `"any"` loads it. A receipt store that is a link, or lies under one, is not read. The
  check is `cooktrust.cpp` (host test `tests/test_cooktrust.cpp`, run by `tests/run.sh`).
- **`guid`**: a resource GUID, in .NET `Guid` text form. The GUID bytes match `Guid.ToByteArray()` = the game's
  resource-id order = the bytes of `ItemInfo.objectId`.
- **`type`**: a qualified reflection name (`keen::UiTextureResource`) hashed FNV-1a-32, or `"0x<hash>"`.
- **`mode`** (resource): `override` (the key must exist in the game), `new` (it must not), or omitted (either).
- **`patch`** (resource): writes a referenced blob's 16-byte `ContentHash` at `offset` in the resource bytes before
  serving — so a cooker need not precompute the hash; it emits the offset of the `ResourceContentReference.hash` field.
- **`blob`** with a `file` only: a new blob (hash derived from the bytes). With a `hash`: the file is served under that hash, whether or
  not the game has it: an override of a game blob, or a new blob under a hash you chose (shader code must use the game's own `ContentHash`,
  [docs/re/vfx.md](../../re/vfx.md) §6). The file length must equal the hash's size, else the entry is an error
  (`blob <file>: its hash says <n> bytes, the file has <m> (the size is decimal)`). The text is the one `ContentHash.ToString()`
  writes: **the size in decimal**, then the three hash words in hex (1 to 8 digits each), with nothing before, between or after
  (`7170-f308b489-d7bff57f-264db8de`). A `hash` that is not a string or not that text makes the entry an error
  (`blob <file>: bad hash … (want <decimal size>-<h0>-<h1>-<h2>)`); the other entries still load. Up to 0.2.0-dev.31.21 a
  hash kiln.assets could not read (a size in hex, say) was served under the hash derived from the bytes, with no error, and
  the game then found nothing under the hash the mod named.
- Files no entry names are ignored (e.g. sources, a cook recipe).

## C# (mods)
```xml
<ProjectReference Include="…/managed/services/Kiln.Assets/Kiln.Assets.csproj" Private="false" ExcludeAssets="runtime" />
```
```csharp
using Kiln;

public override void OnLoad()
{
    if (!Assets.Available) return;                            // off in safe idle / unsupported build
    Assets.RegisterIcon(itemGuid, "icons/frost.png");         // client; PNG path (abs or rel to kiln) or byte[]
    Assets.OverrideTexture(new AssetKey(texGuid, AssetKey.UiTextureType), File.ReadAllBytes(pngPath));
    var hash = Assets.AddBlob(meshBlob);                      // ContentHash to put in a resource reference
    Assets.Register(new AssetKey(guid, "keen::RenderModel"), resourceBytes, mustBeNew: true);
    Assets.Override(new AssetKey(guid, AssetKey.ItemInfoType), editedItemInfo);
    byte[]? bytes = Assets.ReadResource(new AssetKey(guid, AssetKey.ItemInfoType));  // override if any, else vanilla
    var keys = Assets.ListResources(AssetKey.UiTextureType);  // game's + new
    var item = Assets.Reflect(new AssetKey(guid, AssetKey.ItemInfoType));  // the same resource as a reflected struct: item["maxStackSize"]
    IReadOnlyList<BlobEntry> mine = Assets.ListBlobs();       // the blobs this mod added or overrode (ListAllBlobs: every mod's)
    string report = Assets.LoadManifest(Context.Directory);   // register this folder's assets/assets.json
    string dumpPath = Assets.Dump($"{guid}:keen::RenderModel");
}
```
- Everything a mod registers is owned by it and reverted when it unloads (`Assets.RemoveIcon/RemoveResource/RemoveBlob`
  do it sooner). Calls may be made from any thread; game lookups are thread-safe reads.
- Since 0.2.0-dev.28 a native owner the core revokes (a plugin whose `kiln_plugin_main` failed, say) loses its resources,
  blobs and icons without anyone calling `revert_owner`: from the revocation on its registrations are refused
  (`KILN_ERR_REVOKED`), and what it had registered is taken off moments later, by kiln.assets' next call or its own
  cleanup thread (until then the game may still be served its bytes, for a few milliseconds). A `revert_owner` before
  or after does nothing more.
- `Assets.Reflect(key)` and `ReflectedResources` read any resource by field path, find resources by a field's value, and change a
  field claim-backed and reversible: [typed-fields.md](../modding/typed-fields.md#any-resource). `ListBlobs` lists what this
  mod supplied through `Assets`; blobs from `assets.json` and the game's own are not listed: the
  [content blob catalog](#the-content-blob-catalog) lists every one.
- `Assets.IconStateOf(guid)` → `None` / `Pending` / `Served` / `Failed`: content code aliases a new item's GUID to its
  base item's only until `Served` (the UI cache is fatal on a key it was never told about). kiln.content already does
  this; a mod does not need to.
- `Assets.Status()` returns JSON (hooks on, counts, container, reads, and the catalog's generation and last change).

## The content blob catalog
Since 0.2.0-dev.28. Every content blob the game serves, and where each one comes from: the game's own content index and
every live registration (`assets.json`, `AddBlob`/`OverrideBlob`, native callers), one entry per hash. It is the C#
equivalent of ShroudForge's file-content listing and EML's `get_all_contents` as a **live served catalog** (what the game
reads now), not a listing of the archive on disk. Sample: `mods/samples/BlobCatalog` (`/blobs`).
```csharp
var opened = Assets.OpenBlobCatalog();                     // BlobCatalogMode.RequireComplete: the game's index + registrations
if (!opened.Ok) { Log.Warn(opened.Reason); return; }       // a budget would be exceeded: a result, never a truncated list
using var snapshot = opened.Snapshot!;                     // immutable: registrations made later do not show in it
for (ulong at = 0; at < snapshot.Total;)
{
    var page = snapshot.ReadPage(at);                      // up to 256 entries, sorted by (size, hash0, hash1, hash2)
    foreach (var e in page) { /* e.ContentHash, e.Origins, e.SelectedOrigin, e.Owner, e.Sequence, e.Overridden */ }
    at += (ulong)page.Count;
}
byte[]? bytes = Assets.ReadBlob(hash);                     // the bytes the game reads for one hash now
byte[]? off = Assets.ReadResidentBlob(hash);               // the same from a worker: null until the blob is resident
```
| Member | Threads | Answers |
|---|---|---|
| `Assets.OpenBlobCatalog(mode)` | any (from mod code: the mod owns the snapshot) | `BlobCatalogOpenResult(Ok, Reason, Snapshot, Info)` |
| `BlobCatalogSnapshot.ReadPage(first, capacity)` | any | bounded copies of entries `first..`; empty when `first == Total` |
| `BlobCatalogSnapshot.Info`, `Total`, `Dispose()` | any | the header (generation, total, status, completeness); closes it |
| `Assets.ReadBlob(hash)` | any; a worker reads with `ReadResidentBlob` (below) | a registration's bytes, else the game's own from its container; null when no blob has the hash |
| `Assets.ReadResidentBlob(hash)` | any (since 0.2.0-dev.29.16) | the same once the blob is resident; null when it is not |
| `AssetsCatalogChangedEvent(Generation, Reason)` | tick thread | the catalog changed (registration, removal, a mod's unload, the game's index became readable) |

- **What an entry says.** `Origins` has `Game` when the game's index lists the hash (also when a mod overrides it), plus
  the origin of every live registration: `Manifest` (a mod's `assets.json`, its icons and textures too), `Managed`
  (`AddBlob`, `OverrideBlob`), `NativeApi` (any other caller, and icons and textures registered by call).
  `SelectedOrigin`, `Owner` and `Sequence` name whose bytes the game reads now: the latest registration of the hash
  (kiln.assets' rule, unchanged), else the game's own (owner 0, sequence 0). `Overridden`: the game lists the hash and a
  registration's bytes are served in its place. Origins are descriptive: a native caller says what it is.
- **Modes.** `RequireComplete` (the default) needs the game's content index; before the game has opened it the call
  throws `KilnUnavailableException("assets.blob_catalog")`. `RegistrationsOnly` lists the registrations alone and says so:
  `Status = Incomplete`, `GameComplete = false`.
- **Reading a blob off the game's threads.** `ReadBlob` asks the game for its content index itself while the game has
  not opened it yet, and only the game's own threads may do that (its loading can own the index's reader then): call it
  from OnLoad, a tick or a UI callback. A worker thread or a task calls `ReadResidentBlob` (kiln.assets'
  `blob_read_resident`), which never asks the game for anything. It reads a **resident** blob: a live registration
  (`assets.json`, `AddBlob`/`OverrideBlob`, a native caller), resident from the moment it is made, or a game blob the
  content index the game has already opened lists. Resident is not "in memory": a game blob's bytes (that blob's only)
  are read from its container file on the calling thread, which on a hard disk the game is loading from can take a
  while, so keep it off a frame. Once the game has opened its index the answer is `ReadBlob`'s; before that a game blob
  is not resident and the answer is **null**, as for a hash no blob has: try again later (the `AssetsCatalogChangedEvent`
  with reason `Ready` says the index became readable). It throws `KilnUnavailableException("assets.content")` without
  kiln.assets or with one older than 0.2.0-dev.29.15. The test kit models it: `KilnHarness.Assets.IndexReady = false`
  hides the game's blobs from it, not from `ReadBlob`.
- **Budgets.** 1,000,000 entries and 64 MiB per snapshot, 128 MiB for every snapshot held, 64 open at once. Over a budget
  the result is `Ok = false` with `Info.Status = LimitExceeded` and the reason; nothing is opened. The server build
  f0d824be's index lists 39,818 blobs, the game client's about 101,000.
- **Lifetime.** A snapshot never changes; two opens at the same generation share one. Dispose it when done; a mod's open
  snapshots close when it unloads, and a read after that throws `ObjectDisposedException`. The event is posted by a
  check every 25 steps, only while someone subscribes; several changes between two checks are one event.
- **JSON.** `JsonSerializer.Serialize(info)` / `(entry)` writes the shape of
  [`schema/blob-catalog.schema.json`](../../../schema/blob-catalog.schema.json) (uint64 fields as decimal strings).
- **Capability** `assets.blob_catalog` (Kiln.Assets' row): kiln.assets' table has the catalog and its content hooks are
  on. It is open on servers and in every game since 0.2.0-dev.29 (lab-proven on a server).
  Check `13-assets.catalog-client` still has not confirmed it in a real game client.
- **Cost and lifecycle limits.** Opening a catalog synchronously copies and sorts metadata and can block its caller; prefer a worker thread for opening. Snapshot budgets exclude temporary merge and sorting memory. The native index pointer is kept for the process; its validity across hosted leave/join/reload remains unconfirmed, even though each completed snapshot owns its copies.
- Registrations made through `AddBlob` and `OverrideBlob` are tagged `Managed` (a kiln.assets older than 0.2.0-dev.28
  records them as native). Which bytes are served did not change.

## C (native plugins / services)
```c
const KilnAssetsApi* a = api->service((KilnStr){"kiln.assets", 11}, 1);   // in on_ready
if (a) a->icon_register_file(owner, item_guid, (KilnStr){"icons/x.png", 11});
```
Full contract: the header. The **icon-resolver** part (`icon_register_png/file`, `icon_remove`, `icon_state`) is the
first four functions after the header; content-data uses `icon_state` in its UI-texture hook. `resource_read`
(7th function) is callable from any thread and from both world threads and stays at its slot — content-data's ref
resolver calls it. `resource_data(key, &ptr, &len)` (appended last) gives a **zero-copy, read-only** view of a
resource's live loaded bytes (a Kiln override/new resource if registered, else the game's resident copy), valid until
the next tick — for workers that read lots of game resource data cheaply (gamedata, abilities, creatures). Do not
write through it.

Since 0.2.0-dev.29.15 (check `header.size >= offsetof(KilnAssetsApi, texture_rgba) + sizeof(void*)`): `texture_rgba(key,
min_side, rgba, cap, &w, &h)`, a texture resource's pixels as the game reads them now (`keen::UiTextureResource` or
`keen::RenderTextureResource`, a retexture or a mod's icon included), one mip level (the smallest whose sides are both at least
`min_side`) decoded to RGBA8 (RGBA8, BGRA8, R8, BC1, BC3, BC4, BC5, BC7; others `KILN_ERR_INVALID_ARGUMENT`). A level no mip
fits (longer side over twice `min_side`: a mod's PNG icon has one level up to 512) is box-scaled down so its longer side is
`min_side`, its shape kept; the size answered is that size. Ask the size first (`rgba` NULL: `KILN_ERR_BUFFER_TOO_SMALL` with
the size, no pixel read). It reads that one blob (an item icon's chain is 87 KB), without asking the game for its content
index (as `blob_read_resident`). The game's resident bytes are copied under a fault guard: a resource the game dropped
meanwhile is `KILN_ERR_NOT_FOUND`, as is any `resource_read` of the game's own bytes that cannot be read.

`blob_read_resident(hash, buffer, capacity, &len)` (since 0.2.0-dev.29.15, after `texture_rgba`; check `header.size >=
offsetof(KilnAssetsApi, blob_read_resident) + sizeof(void*)`): `blob_read` for callers off the game's threads. `blob_read`
asks the game's content-map accessor itself while the game has not opened its content index, which only the game's own
threads may do (loading can own the reader then); `blob_read_resident` answers `KILN_ERR_NOT_FOUND` until the game has opened
it, and is otherwise the same. Kiln.Items reads the game's texts with it (`Items.DisplayName`). Mods call it as `Assets.ReadResidentBlob` (since
0.2.0-dev.29.16, [above](#the-content-blob-catalog)). `texture_rgba` is not a managed API: Kiln.Ui draws the game's item
icons with it ([ui.md](../ui.md#item-icons-and-names)), and Kiln writes the pixels nowhere. The pure part (layouts, the level, the decoders) is `native/services/assets/texture_rgba.h`,
host-tested by `tests/test_texture_rgba.cpp` (`tests/run.sh`).

Since 0.2.0-dev.28 (check `header.size` before each): `blob_add_ex` / `blob_override_ex` are `blob_add` /
`blob_override` with the registration's origin (`KILN_BLOB_ORIGIN_MANAGED` or `_NATIVE_API`; anything else is
`KILN_ERR_INVALID_ARGUMENT`), and `blob_catalog_open` / `blob_catalog_read` / `blob_catalog_close` are the
[content blob catalog](#the-content-blob-catalog) (`KilnBlobCatalogInfo`, 32 bytes; `KilnBlobCatalogEntry`, 48 bytes;
statuses, reasons and result codes in the header). `blob_add` and `blob_override` tag `NATIVE_API`, `load_manifest`
tags `MANIFEST`.

### Loaded resources from tasks
Since 0.2.0-dev.30.3 (check `header.size >= offsetof(KilnAssetsApi, loaded_list) + sizeof(void*)`; capability
`assets.loaded`): `loaded_snapshot(owner, key, bytes, capacity, &required, &info)` and `loaded_list(owner, type, out,
capacity, &required, &info)` read the resources the game has loaded **now**, from any thread, on the exact game builds
only (kiln.engine's profile of the running exe and its resource layout; client 26430625 and dedicated server 26430253).
They are what kiln mode's `game.assets.get_loaded_resource` and `get_loaded_resources_by_type` stand on.
- **In a world only.** The game's resource service must be bound to the running world's item registry. In the main menu,
  and while a world loads or unloads, both answer `KILN_ERR_UNAVAILABLE` with `KilnLoadedInfo.status`
  `KILN_LOADED_NOT_READY`.
- **A copy, never a pointer.** A snapshot is copied twice and compared, then its key is looked up again: if anything
  moved meanwhile the answer is `KILN_LOADED_CHANGED` (ask again), never a torn or partial copy. A listing copies the
  game's resource index twice and compares the copies. Nothing is written and no engine gate is taken.
- **What the game reads.** A key kiln.assets serves (an override of a game resource, a mod's new resource) answers its
  served bytes with `KILN_LOADED_SERVED` in `info.flags`; a listing has the mods' new keys after the game's own, each
  once. `info.handle` is the game's 1-based handle of the key (0 for a mod's new resource).
- **Missing is not unavailable.** `KILN_ERR_NOT_FOUND` (`KILN_LOADED_MISSING`) means the service is ready and has no such
  key; a key looked up while the index changed is `KILN_LOADED_CHANGED`.
- **Buffers.** `KILN_ERR_BUFFER_TOO_SMALL` sets `*required` and `info.bytes` and copies nothing; sizes can change between
  calls, so ask again with the larger buffer. `KilnLoadedInfo` is 24 bytes; set `info.size` first.
- Bounds: a resource of at most 256 MiB (the profile's limit), a listing of at most the profile's 1,048,576 index entries
  plus the mods' new keys. A listing leaves out a key whose bytes are not committed, readable memory.

**Writing one scalar** (since 0.2.0-dev.30.3, after `loaded_list`; capability `assets.loaded_write`):
`loaded_scalar_compare_exchange(owner, key, expected_session, expected_full, resource_size, offset, desired, width, &reply)`
writes one naturally aligned 1, 2, 4 or 8-byte scalar of a loaded resource, only while the whole resource is still exactly
`expected_full` (a `loaded_snapshot`'s bytes) and the server world's session is still `expected_session`. One hold of
kiln.engine's write gate (the server world of a host or a dedicated server, between frames, as kiln.world's ECS writes)
with the content worker's lifetime guard borrowed from it: the resource looked up again, compared in full, its page made
writable for the one scalar, the scalar compare-exchanged, the whole resource compared again and the page protection put
back. `reply` is kiln.world's `KilnMutationReply` (set `reply.size`): `KILN_ECS_OK` written, `CONFLICT` the resource or the
scalar changed (nothing written), `STALE` another session (also one that began during the call), the resource moved or is
not `resource_size` bytes now, `BUSY` the gate was busy (try at a later step), `NOT_WRITABLE` a key kiln.assets serves (an
override or a mod's new resource: the game reads that copy, so change the mod's copy instead) or a `resource_size` over the
profile's limit, `UNAVAILABLE` no exact profile, a joined client, no world, the gate or guard refused, or the scalar's page
is not one Kiln makes writable (a private committed read-only or read-write page of the content worker's data).
`UNCERTAIN` (the page protection could not be put back, the exchange faulted, or the resource changed during it) latches
every later write of the process: those answer `UNCERTAIN` with `KILN_ECS_WRITE_WHY_TARGET_LATCHED` and attempt nothing. Not a transaction: one scalar per call. Every committed write
is recorded with its owner, key, offset, width, session and a write id (the last 256 in `status`'s `"loadedWrites"`) and
logged in kiln.log.

**Naming who wrote** (since 0.2.0-dev.32, after `loaded_scalar_compare_exchange`; check
`header.size >= offsetof(KilnAssetsApi, loaded_write_records) + sizeof(void*)`):
`loaded_scalar_compare_exchange_named(owner, key, expected_session, expected_full, resource_size, offset, desired, width,
writer, field, &reply)` is the same write, whose record also names `writer` (who the write is for) and `field` (the
caller's own words for the field, for diagnostics). Each is up to 127 bytes of UTF-8 without a control character; an
empty `writer` names nobody, as the unnamed member. A name that is not such text is `KILN_ERR_INVALID_ARGUMENT` before
anything happens. The name is what the caller says, not a security claim. Kiln's interpreter of ShroudForge mods
(kiln-sfi, kiln mode) names each package's writes `shroudforge:<package id>`, so kiln.content's registry report names
that package on the field it changed at run time instead of "another writer" (see
[registry.md](registry.md)).
- `loaded_write_records(out, record_size, capacity, &required)` copies one `KilnLoadedWriteRecord` (336 bytes) per
  **place** (a resource's key, offset and width) for its newest committed write, oldest first: its id, owner, session,
  the place, the writer and field it named (`""` none), `after` (the value the newest write left) and `before` (the value
  its run began over: consecutive writes there by the same owner and writer, each over the value the one before left).
  Any other write starts a new record, an unnamed one too, so a place is named only while its newest writer named itself.
  `KILN_ERR_BUFFER_TOO_SMALL` sets `required` and copies nothing; `record_size` must be at least 336 (bytes past it are
  zeroed). A copy under kiln.assets' own lock, any thread, no call out.
- Records live for the process (the game keeps its loaded resources across worlds): at most 16,384 places, the place
  written longest ago dropped first (kiln.log says so once). `status`'s `"loadedWrites"` rows carry `"writer"` and
  `"field"` when a write named them, and kiln.log's line names them.

Adapted from ShroudForge by bonsaibauer, MIT (its loaded ResourceService reads and its guarded resource scalar update).
The portable part is
`native/services/assets/loaded.{h,cpp}` (the write records too: `WriteRecords`), host-tested over a fake engine by
`tests/test_loaded.cpp` (`tests/run.sh`, ASan and TSan; the CMake target `assets-loaded-test` builds the same test as a
Windows program).

## Icons for model overrides
The game's icons are baked: a `keen::UiTextureResource` per item GUID (BC7 256×256 with mips on client 26430625),
never drawn from the model at run time. So a mod that changes a model ships the matching icon:
- `{"kind": "icon", "guid": "<item GUID>", "file": "icons/x.png"}` in assets.json (or `Assets.RegisterIcon`) works for
  both kinds of item. For a **game item** kiln.assets retextures the item's own UiTextureResource (the PNG resized and
  encoded to its format; kiln.log "icon icons/cube_sword.png: 1d622bd9-… is a game item: its own icon texture is
  retextured"); at boot, before the resource service is up, the icon is registered as new and switched over when it
  appears. For a **new item** it is a new UiTextureResource (icon state `Served` once the UI cache knows it). New items
  whose icon is aliased to their base (kiln.content) follow a retextured base.
- A **default icon** made from the model: `tools/assets/render-icon.py MODEL.gltf OUT.png [--uv-colors …]` renders the
  same glTF the model was cooked from (orthographic three-quarter view, a weapon along +Y drawn diagonally, lit, with
  an outline and a soft shadow, transparent background, 256×256; pure Python). `--uv-colors 'u0,v0,u1,v1=#rrggbb;…'`
  colours triangles by the UV region they use (models that point into a game texture's regions). ModelSample's
  `build-assets.sh` renders its two icons with it.
- Registered after boot, a game item's retexture shows once the UI loads that texture again (the next start at the
  latest); put icons in assets.json.

## How it runs
- At boot (hooks on): kiln.assets hooks the resource service slots (`assets.resources`), the content reader and the
  async IO queue (`assets.content`), and on the client the UI texture cache's populate (`assets.ui_icons`). It adds one
  empty container (`kiln/data/assets/kiln-content.dat`) to the IO queue and answers reads of it from memory.
- `assets/assets.json` for every mod in the load order is applied before the game's UI builds, so boot-time icons and
  overrides are in place from the first frame. Mods come from the active profile (`kiln.profile`); `kiln/mods/*` is
  also scanned when there is no profile, or when the profile opts its drop-ins in (`dropins() & (1<<KIND_KILN)`), like
  Kiln.Host. Icons register immediately; a `resource`/`blob` entry registers and is served once the game opens its
  container (during resource-system init); a `texture` entry is queued at boot and applied when the resource service
  appears (it must read the vanilla texture first) — all still before the UI draws.
- A new resource gets a synthetic handle and joins its type's list; an override keeps the game's handle and swaps the
  bytes. A new content blob's hash is `SHA-256(bytes)[0:12]` + the size; an override reuses the game's hash. The game
  does not re-hash ordinary content at run time, so mod blobs are accepted. Shader code is the exception: the pipeline streamer checks it
  against the real `ContentHash` ([docs/re/vfx.md](../../re/vfx.md) §6.3).
- Icons registered after boot become visible within a few UI frames (kiln.assets makes the cache refill once there is
  room). Overrides of a resource the game already uploaded (a UI texture, a streamed mesh) apply at the next load/
  eviction — so put visual overrides in assets.json.

## Limits
- **Client only** for anything drawn (icons, textures, meshes). The dedicated server verifies the resource/content
  plumbing but shows nothing visual, and has no `RenderModel` at all.
- **Already-uploaded GPU resources** keep their old pixels/geometry until evicted; assets.json (boot) avoids the race.
  There is no forced reload of a live texture atlas entry in v1.
- **Formats**: encodes RGBA8/BGRA8, BC1, BC3, BC4, BC5, BC7. BC6H (HDR) and BC2 are not encoded. A retexture to an
  unsupported format fails with a logged reason.
- **The player's animation graph is reserved** for kiln.playeranim (GUID `21319e83-5087-4fff-8795-22997843649e`,
  `keen::anim_graph::runtime_graph::AnimationGraphResource2_0`, part 0), from process start, also when kiln.playeranim
  is off or has nothing to merge. `resource_put` answers `KILN_ERR_UNAVAILABLE` for it (and, once kiln.playeranim has
  put the merged graph, `resource_remove` too), an
  `assets.json` entry for it is refused at boot with a kiln.log line naming the key, and `Assets.Override` /
  `Assets.Register` throw `ArgumentException`. Add states in `assets/player-states.json` instead
  ([playeranim.md](playeranim.md)). `KILN_ASSET_PUT_PIN` is for native services only; kiln.playeranim is its one caller.
- **No save impact**: nothing is written to the game's files or the save; mod resources exist only while the mod runs.
  A removed mod's resources come off; items that referenced only a mod's resources fall back to the vanilla look/name.
- The merged content map the game is handed, and the per-type key lists it is given, are kept for the session: the game holds on to them.
  They are made again only when a blob, or a resource of that type, changes.
- What the hooks read is published as immutable indexes and freed once no reader can hold them (reader counts by epoch). A put adds to a
  small index of recent resources, and the main one is built again after 64 of them, so a put costs about the same however many resources
  are served: 3,097 template, sequence and perk clones put one at a time take 0.2 s, and the server stays at 1.3 GB.

## Tests and labs
```sh
export KILN_OUT=<build dir>
native/build.sh kiln-core kiln-proxy kiln-assets
dotnet build mods/tests/assets-test/AssetsTest.csproj -c Release -o $KILN_OUT/mods/assets-test --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new assets 26060 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/assets --policy any --samples
tools/lab/lab.sh start assets        # assets-test: 8 PASS, 0 FAIL (resources, blobs, overrides, dump)
```
The catalog (0.2.0-dev.28): `native/services/assets/tests/run.sh` (host, no game: provenance, the served winner,
`PASS assets.catalog-snapshot`); `managed/tests/Kiln.Api.Tests/BlobCatalogTests.cs` over the test kit's kiln.assets table
(`managed/Kiln.TestKit/Fakes/Assets`; not in a `KilnHarness` yet: see the note there); in a lab, before the start,
`python3 mods/tests/assets-test/catalog_fixture.py <lab> <lab>/kiln/data/assets-test/catalog-fixture.json` (the game's
index read by Kiln's own KFC reader), then assets-test logs `PASS assets.full-catalog`.
Client (icons, retextures, meshes): deploy with
`tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/asset-sample`.

## Manual test (client 26404966)
1. A new item's `assets.json` icon (AssetSample's Emberseal) shows its PNG in the backpack (tooltip
   "Emberseal — Materials — Level 5 Rare") and in the hand-crafting menu; NewItems' Glacier PNG shows on the hotbar,
   in the backpack and in the crafting menu. No "corrupted game ui texture" crash.
2. The Powder Ball T1 retexture (`010932d7-…`, BC7 256×256, 9 mips) shows the new image on the hotbar, in
   the backpack and on its crafting recipe.
3. ModelSample's RenderModel overrides render: the Bronze Sword shows the cube blade in hand, and the new
   Kiln Tower Sword (crafted from 1 twig) shows its stacked-block model.
4. Icons for model overrides: ModelSample's `"kind": "icon"` entries, rendered
   from the cooking glTF by `tools/assets/render-icon.py`. The Kiln Tower Sword (new) showed its tower icon in the
   crafting menu and on the hotbar; the Bronze Sword's own icon texture was retextured (seen through Frost Edge, a
   NewItems clone whose icon aliases to the Bronze Sword's texture, which showed the cube sword).
5. Open: the spiral handedness of the cube blade (the game camera stays behind the character; synthetic pointer moves
   don't rotate it), and unloading the mod in a live session.

`side`: `client` on an entry skips it on a dedicated server (soundcook writes it on sounds and cues, in the manifest the mod ships). A model's entries need no mark: a server skips them by their type.
