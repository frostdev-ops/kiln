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
    { "kind": "blob", "hash": "<size>-<h0>-<h1>-<h2>", "file": "retex/pixels.bin" }
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
  [docs/re/vfx.md](../../re/vfx.md) §6). The file length must equal the hash's size.
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
- `Assets.Reflect(key)` and `ReflectedResources` read any resource by field path, find resources by a field's value, and change a
  field claim-backed and reversible: [typed-fields.md](../modding/typed-fields.md#any-resource). `ListBlobs` lists what this
  mod supplied through `Assets`; blobs from `assets.json` and the game's own are not listed.
- `Assets.IconStateOf(guid)` → `None` / `Pending` / `Served` / `Failed`: content code aliases a new item's GUID to its
  base item's only until `Served` (the UI cache is fatal on a key it was never told about). kiln.content already does
  this; a mod does not need to.
- `Assets.Status()` returns JSON (hooks on, counts, container, reads).

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
