# The loaded-resource service and the content reader (evidence for kiln.assets)

How the game keeps its 91,812 resources resident and streams its 101,459 content blobs, and the functions kiln.assets
hooks to serve mod resources, overrides and content without changing any game file. Builds: dedicated server svn
1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`, image base 0x140000000) and the client Steam 26404966
(`enshrouded.exe` sha256 `ce6fdba6…`). RVAs below are the client's (image base 0x140000000) with the server's in
brackets. Sources: EMF 0.3.3 `re_content.md` §1 (MIT), the ShroudForge MIT provenance
`docs/evidence/game-builds/loaded-resource-service-provenance.de.md` and its MIT reader
`modules/game-runtime/native/loaded-resources/` (ported facts, credited here), the game's own reflection, capstone +
Ghidra over scratch copies of both exes, the clean-room KFC4 reader `tools/kfc`, and runtime checks in lab `assets`
(port 26060, a copied world, no player). Nothing from `kfc-parser` (GPL). Tags: **[dis]** disassembly,
**[cap]** typed-hole capture in `sigdb/symbols/{server,client}.d/assets.json`, **[refl]** the game's reflection,
**[rt]** runtime (kiln.log quoted).

## Short answers
- **Two stores.** A *resource* is a reflected struct the game keeps resident (every byte of `*.kfc_resources` is
  decompressed into a ~400-450 MB read-only private region at start; EMF §1.1). A *content blob* is raw bytes
  (texture pixels, meshes, loca tables) streamed on demand from `enshrouded_NNN.dat` by an async IO queue. Resources
  reference blobs through `keen::ResourceContentReference{ContentHash hash, …}`.
- **Resources are keyed by a 32-byte ResourceId** `{GUID[16], u32 typeHash, u32 part, u64 reserved=0}`; the engine
  compares the first 24 bytes. kiln.assets uses the 24-byte key `{GUID, type, part}` (`KilnAssetKey`). The GUID bytes
  are in the game's order = .NET `Guid.ToByteArray()` = the bytes of `ItemInfo.objectId`. **[rt]** the Bronze Sword
  (`1d622bd9-8170-41b1-822e-d3984b386d95`) resolves to itemId `0x43108313` = FNV-1a-32 over those bytes, confirming
  the order.
- **key → handle → data.** A resource service object `S` holds function pointers in slots. The whole engine looks
  resources up through those slots, so hooking the slot targets covers every lookup.
- **Content blobs** are looked up by `keen::ContentHash {u32 size, hash0, hash1, hash2}` in the KFC content map, then
  read from the container file by the async IO queue (IOCP + `ReadFile`).
- kiln.assets serves new/overridden resources and blobs **from memory**: it answers the key/handle lookups, adds one
  extra container to the IO queue (an empty file), rewrites the content map so mod hashes point into that container,
  and answers the game's `ReadFile` on that one handle from memory. No game file is touched.

## 1. The resource service object `S` and its slots **[dis] [cap]**
`S` is created once at app start by the resource-system init (`ResourceInit`, client 0x107f2d0 [server 0x828510]), the
function that logs `"Could not create resource system! error=%s"`. Its caller (the resource-system constructor, client
0x107d7d0) fills a table of function-pointer slots in `S` **[dis]** and then calls `ResourceInit`. The slots Kiln uses:

| slot in `S` | sigdb name | client [server] | signature / job |
|---|---|---|---|
| `+0x7d0` | `assets/ResourcesByType` | 0x1081650 [0x82a8a0] | `TypeList* (out, S*, u32 typeHash, u32* opt)`: the resident ResourceIds of a type (used to build the UI texture key set) |
| `+0x7d8` | `assets/ResourceByKey` | 0x1080a00 [0x829c50] | `u32* (u32* out, S*, const ResourceId* key)`: key → handle (1-based; 0 = not resident) |
| `+0x7e0` | `assets/ResourceKeyOf` | 0x107ec80 [0x827ec0] | `KeyOfResult* (out, S*, u32 handle)`: handle → ResourceId |
| `+0x7e8` | `assets/ResourceData` | 0x10804f0 [0x829740] | `DataSpan* (out, S*, u32 handle)`: handle → `{const u8* data, u64 size, {u32 0, u32 1}}` |
| `+0x808` | `assets/ContentSubmit` | 0x1083130 [0x82c380] | submits content requests; its first call is the content-map accessor |

`ResourceByKey`'s thin wrapper (`mov rax,[S+0x7d8]; call rax`) and `ResourceData`'s (`mov rax,[S+0x7e8]; …`) are the
call sites that capture the slot offsets a second time (`@assets/ResourceService.wrappers`, 2 votes each) **[cap]**. The
slot table itself captures the function rel32s and the offsets (`@assets/ResourceService.slots`) **[cap]**. The reader
object is `*(S+8)`; its metadata is `*(reader+0x60)`; the content map is `metadata+0x48` (ShroudForge §2). On the
server all slots resolve to the same offsets (0x7d0/0x7d8/0x7e0/0x7e8/0x808) — the service is the same engine code.

### Handle algorithm (ShroudForge §3-4, ported, MIT)
`ResourceByKey` hashes the ResourceId (`h = u32le(guid[0:4]); for b in typeLE+partLE: h=(h^b)*0x01000193`), indexes a
power-of-two bucket array (`bucket = h & (n-1)`, 8-byte `{first_index, count}` entries), scans the 32-byte keys of that
bucket range for an exact 24-byte match, and returns `(slot_addr − values_base)/8 + 1`. `ResourceData` turns the handle
into `entry = values_base + (handle-1)*8`, `offset = u32(entry)`, `size = u32(entry+4) & 0xFFFFFF`,
`chunk = u8(entry+7)`, `data = chunkTable[chunk].base + offset` (the chunk table is `*(S+0x30)`). The live map had
91,812 keys / 91,812 values (ShroudForge root check); the current build has 91,909 resource keys (`tools/kfc` over the
client KFC). kiln.assets does not re-implement these: it calls the game's own slot functions (SEH-guarded) to decide
whether a key is resident and to read a resource's bytes.

## 2. What kiln.assets does for resources **[rt]**
- **New resource** (`resource_put` without a game key): kiln.assets gives it a synthetic handle (`0x70000000 + n`),
  answers `ResourceByKey` with that handle, `ResourceData` with the mod's bytes, `ResourceKeyOf` with the key, and adds
  the key to the type's `ResourcesByType` result (the game's list, copied once, plus the new keys).
- **Override** (`resource_put` of a key the game has): kiln.assets keeps the game's handle and answers `ResourceData`
  for that handle with the mod's bytes. `ResourceByKey` / `ResourcesByType` are unchanged (the key already exists).
- Snapshots are immutable and swapped atomically; the hooks read them lock-free. Memory the game may hold a pointer
  into is never freed. **[rt]** lab assets-test: register a 256-byte resource under a fresh GUID+type and read the
  exact bytes back; override the Bronze Sword's ItemInfo, read the edited byte, remove the override, read the vanilla
  byte again — all PASS.

## 3. The content reader and the async IO queue **[dis] [cap]**
`ContentSubmit` (client 0x1083130) is the only submit path; every content request flows through it. Its first call is
the KFC content-map accessor `assets/KfcContentMap` (client 0x125dd00 [server 0x82c380's first callee]): `reader →
mov rax,[reader+0x60]; add rax,0x30; ret` — a `StaticMap` header at `metadata+0x30` **[dis] [cap]** (Kiln checks these
exact bytes before trusting the offsets). The map's three self-relative arrays (offset relative to the field, count
after it): buckets (8-byte `{first,count}`, power-of-two), keys (16-byte ContentHash), values (16-byte
`{u48 offset, u16 container, …}`). `bucket = hash0 & (nb-1)`; a match gives `{offset, container}` **[dis]**.

The async IO queue is created by `assets/AsyncIoCreate` (client 0xf3e410 [server 0x5abed0]); its caller is the resource
init (`@assets/ResourceInit.io` captures the call site) **[cap]**. The config holds the list of container paths
(`%s_%03u.dat`); `AsyncIoCreate` opens each with `CreateFileW(FILE_FLAG_OVERLAPPED)`, binds it to an IOCP, and a
worker thread issues `ReadFile` with an `OVERLAPPED` offset and drains completions with `GetQueuedCompletionStatus`.

### What kiln.assets does for content **[rt]**
1. In `AsyncIoCreate`, when the path list is the KFC containers (first path ends `_000.dat`), Kiln appends one path:
   an empty `kiln/data/assets/kiln-content.dat`. Its index is the old container count. **[rt]** "async IO queue … 10
   game container(s) + Kiln's as #10".
2. IAT hooks on `CreateFileW` / `CreateIoCompletionPort` / `ReadFile` (same technique as kiln.ui): Kiln records the one
   handle of its container and the IOCP it joins, and answers only that handle's `ReadFile` from memory (serving the
   requested byte range from the mod blobs), posting the completion to the IOCP exactly as a real overlapped read does.
   **[rt]** "the game opened Kiln's container (handle …)", "Kiln's container joined the IO completion port … (key 10)";
   assets-test read a 4096-byte blob back byte-for-byte through this path.
3. `KfcContentMap` is hooked to return a merged map (built once per content generation): the game's entries, with
   overridden hashes re-pointed into Kiln's container, plus the mod hashes. Kiln verifies the game map reads the way it
   assumes (bucket layout) before trusting it. **[rt]** "content map rebuilt: N game blobs + M Kiln entries".
   Content hash rule (so blobs can be precomputed offline): `size = len`, `hash0..2 = SHA-256(bytes)[0:12]` little-
   endian. The game does not re-hash ordinary content at run time (its "[kfc] Wrong content hash" check is a verify mode only),
   so any unique hash is accepted; Kiln refuses a hash that collides with a different game blob. Shader code is the exception: the pipeline
   streamer recomputes the real `ContentHash` (`docs/re/vfx.md` §6).

## 4. The UI texture cache and icons (client only) **[dis] [cap]**
The UI texture cache (object at a fixed field of the UI app) renders every `UiTextureResource`. Its key is
`{GUID[16], type u32 (0xf9e86997 = UiTextureResource), part u32}`; a key it does not know is **fatal**: the get
`0xa4e5f0` ends in `FUN(…,"corrupted game ui texture: %k"); *flag=0x2a; crash` (`content.ui_texture`, kiln.content
hooks the get). The cache fills its key set once, in `assets/UiTexturePopulate` (0xa560a0): it returns at once while
the byte `cache + 0x36c0` (`uitex.populated`) is 1; otherwise it lists every `UiTextureResource` through
`ResourcesByType` (`cache + 0x18` is `S`, `uitex.resources`) and inserts each unknown key into the cache's map at
`cache + 0x3548` (`uitex.map`); a free-slot counter is at `cache + 0x3510` (`uitex.free_count`) **[dis] [cap]**.

kiln.assets serves an icon as a **new resource** (a 56-byte `UiTextureResource`, RGBA8, 1 mip, `format=37`) plus its
pixel **content blob**, keyed by the item's GUID. Because the new key is in `ResourcesByType`, `UiTexturePopulate`
inserts it — but only when it refills. kiln.assets hooks `UiTexturePopulate` and, when a new icon is registered after
boot, clears `uitex.populated` (if `uitex.free_count` leaves room; the cache keeps room for extra keys) so the game
refills its key set itself, on its own thread. Icons in a mod's `assets/assets.json` are registered natively at boot,
before the UI builds, so they are in the first key set and never need a refill. `icon_state(guid)` reports NONE /
PENDING / SERVED / FAILED so kiln.content's icon hook aliases a new item's GUID to its base only until SERVED (the
cache would crash on a key it lacks). kiln.assets never hooks `0xa4e5f0`; that stays kiln.content's single hook.

Layouts from reflection (`tools/kfc decode`): `keen::UiTextureResource` 56 B `{ResourceContentReference data(32),
string debugName@0x20, uint2 size@0x28, TextureType type@0x30, PixelFormat format@0x31, u8 levelCount@0x32,
u8 isTiled@0x33}`; `keen::RenderTextureResource` 72 B `{u16 width, u16 height, u16 depth, u16 layerCount,
u8 levelCount@8, TextureType@9, PixelFormat format@0xa, …, ResourceContentReference content@0x10, …, string
debugName@0x3c}`; `keen::ResourceContentReference` 32 B `{ContentHash hash(16), HashKey32 category@0x10, u32
bundleId@0x14, u64 sortKey@0x18}`; `keen::ContentHash` 16 B `{u32 size, hash0, hash1, hash2}` **[refl]**.
`keen::PixelFormat` values Kiln encodes: 37 R8G8B8A8_unorm, 43 srgb, 44/50 BGRA; 131-134 BC1, 137/138 BC3,
139/140 BC4, 141/142 BC5, 145/146 BC7 **[refl]**.

## 5. Resident vs streamed
- **Resources** are all resident (one read-only region, loaded at start; EMF §1.1, ShroudForge root check). An
  override of a resource the game has already read takes effect only the next time the game reads it: for a
  `UiTextureResource` that is at the next cache refill (or the next start); so icons/overrides belong in assets.json
  (boot) or in `OnLoad`.
- **Content blobs** are streamed on demand through the IO queue. A blob override is picked up the next time the game
  requests that hash. A GPU resource the client has already uploaded (a UI texture already in the atlas, a mesh already
  streamed) keeps its uploaded copy until evicted; assets.json (boot) avoids the race.

## 6. Sides
- The resource service and content reader exist on **both** sides (the server loads templates, item data, loca). Every
  resource and content symbol resolves on both exes; `assets.resources` and `assets.content` are on for both.
- Icons and the UI texture cache are **client only** (`assets.ui_icons`; a dedicated server never draws). On a server,
  `icon_*` returns `KILN_ERR_UNAVAILABLE`.
- Textures: `UiTextureResource` resources exist on the client (5,699; the 273 item icons that use a 2D image, plus
  menu/HUD images). `RenderModel` / render textures are **client only** — the dedicated-server KFC has no
  `keen::RenderModel` at all (verified with `tools/kfc`), so a mesh override only shows on the client.

## 7. Symbols (`sigdb/symbols/{server,client}.d/assets.json`)
`assets/ResourceInit`, `ResourceByKey`, `ResourceData`, `ResourceKeyOf`, `ResourcesByType`, `ContentSubmit`,
`KfcContentMap` (captured from ContentSubmit's rel32), `AsyncIoCreate`; probes `@assets/ResourceService.slots` (the slot
table), `@assets/ResourceService.wrappers` (the two call wrappers), `@assets/ResourceInit.io` (the AsyncIoCreate call
site). Client overlay adds `assets/UiTexturePopulate` + `@assets/UiTexturePopulate.fields` + `@assets/UiTextureAlloc.fields`.
Constants: `svc.by_type` 0x7d0, `svc.by_key` 0x7d8, `svc.key_of` 0x7e0, `svc.data` 0x7e8, `svc.submit` 0x808; client
`uitex.populated` 0x36c0, `uitex.resources` 0x18, `uitex.map` 0x3548, `uitex.free_count` 0x3510. Features:
`assets.resources`, `assets.content`, `assets.ui_icons`. All resolve on server 1285601 and client 26404966 with
`tools/sigport/sigdb_check.py` (multi-anchor: the slot functions are witnessed by both their own prologues and the
creator's rel32 table; the slot offsets by the creator and the wrappers).

## 8. Open questions / limits
- A GPU resource already uploaded is not re-fetched until evicted (point 5): visual overrides go in assets.json.
- The content-map rebuild keeps old snapshots alive (a reader may hold one) — a small, bounded leak; fine for the
  handful of rebuilds a session does, but hazard pointers would be needed for churn.
- Mesh streaming served from memory (RenderModel renderData) is **verified in a client**: a cube-blade override of the Bronze Sword's RenderModel (9c38c7f0) rendered on the held weapon (the spiral of cubes and
  box grip were visible), with no `[model_loader]` errors. The server has no RenderModel, so this is client-only. See
  docs/re/models.md.
- **Item icons are baked textures (resolved):** every item's inventory/hotbar/crafting icon is the
  `UiTextureResource` keyed by the **item's own GUID** (e.g. `Consumable_Grenade_Explosive_PowderBall_T1` = item GUID
  `010932d7…` = the GUID of its UiTextureResource), cooked offline from `iconModel`; the UI never renders the model at
  run time. So a RenderModel override changes the held/dropped model but **not** the icon: in a client the drawn
  item labelled "Bronze Sword" showed the cube-blade mesh in hand while its hotbar icon stayed the vanilla blade. To
  change an item's icon, override its UiTextureResource (or that resource's blob) — the same path as the Powder Ball
  retexture, which did change the icon in the hotbar, backpack and crafting menu.
- BC6H (HDR) and BC2 are not encoded (no mod use yet); other formats fall back to `KILN_ERR_INVALID_ARGUMENT`.
