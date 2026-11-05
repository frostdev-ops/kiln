# typegen — stable bindings for the game's ECS types (Kiln.Game)

Forge gives mods stable names for Minecraft's fields. Kiln's equivalent is **Kiln.Game**: generated, explicit-layout
C# structs for the engine's ECS components, with type-hash constants and a typed read API, all derived from the
game's own reflection so the offsets are exact on the build they were generated for.

```
game reflection registry ──(walk)──► sigdb/types/<side>/<sha>.json ──(typegen.py)──► managed/Kiln.Game/Generated.cs
   content/TypeByHash              tools/typegen/probe                 tools/typegen/typegen.py
```

## 1. The reflection registry
The engine keeps a reflection record for every `keen::` type, found by `TypeByHash(u32 hash)` where the hash is
FNV-1a-32 of the qualified name (an ECS component is `fnv1a("keen::ecs::" + name)`, the same hash kiln.world uses).
Record layout (RVAs confirmed in `docs/re/settings.md`, `content.md`):

| Offset | Meaning |
|---|---|
| `+0x20/+0x28` | qualified name ptr/len |
| `+0x40` u32 | size; `+0x44` u16 alignment |
| `+0x48` u32 | field (or enum-value) count; `+0x4C` u32 kind (low byte) |
| `+0x50` u32 | hash |
| `+0x58` | **pointer** to the field-record array (stride `0x30`: name ptr, name len `+0x08`, type ptr `+0x10`, offset u64 `+0x18`) |

Kinds: 1 bool, 2 u8, 3 s8, 4 u16, 5 s16, 6 u32, 7 s32, 8 u64, 9 s64, 0xA f32, 0xB f64, 0xC enum, 0x11 typedef,
0x12 struct, 0x13/0x19 StaticArray/BlobArray, 0x1D/0x1C guid/object-ref.

## 2. Walking it (the probe)
`tools/typegen/probe` is a small advanced lab mod that walks the registry **live** through `kiln.hooks`
(`Symbols.Get("content/TypeByHash")` + `Memory.Read`), dumping a list of component types to
`kiln/data/typegen-probe/types.json`. It needs no new native code — it is pure dogfooding of kiln.hooks. Edit its
`Components` list to add types.

```sh
# in a lab with content loaded:
tools/lab/lab.sh new tg 25861 --save <savegame dir>
KILN_OUT=<build dir> tools/deploy-server.sh ~/srv/enshrouded/labs/tg --policy any
dotnet build tools/typegen/probe -c Release -o $KILN_OUT/mods/typegen-probe --artifacts-path $KILN_OUT/dotnet
# copy TypegenProbe.dll + kiln.mod.json into the lab's kiln/mods/typegen-probe and: echo "load typegen-probe" > kiln/control/x.cmd
cp ~/srv/enshrouded/labs/tg/kiln/data/typegen-probe/types.json sigdb/types/server/<sha>.json
```

## 3. Generating (typegen.py)
```sh
python3 tools/typegen/typegen.py sigdb/types/server/<sha>.json managed/Kiln.Game/Generated.cs
```
Each type becomes a `[StructLayout(LayoutKind.Explicit, Size = N)]` struct in namespace **`Kiln.GameTypes`** (the
assembly is `Kiln.Game`; the namespace is not `Kiln.Game` because that collides with the `Kiln.Game` *class* in
`Kiln.Api`). Primitive fields map to C# primitives at their real `[FieldOffset]`; struct/array/guid fields become a
`fixed byte name[size]` buffer (exact layout, reinterpret as needed). Each struct also carries
`public const uint Hash` and `public const string Component`. A `ComponentTypes` class holds every hash as a constant.

```csharp
[StructLayout(LayoutKind.Explicit, Size = 8)]
public unsafe struct NetworkHealth {
    public const uint Hash = 0x216EB5F9;
    public const string Component = "NetworkHealth";
    [FieldOffset(0)] public uint health;      // keen::uint32
    [FieldOffset(4)] public uint healthMax;   // keen::uint32
}
```

## 4. The typed read API
`Kiln.GameTypes.Components` reads components by type over kiln.world (tick thread):

```csharp
using Kiln.GameTypes;
if (Components.Get<NetworkHealth>(entity) is { } nh) Log.Info($"{nh.health}/{nh.healthMax}");
foreach (var e in Components.All<TeleportCount>()) { ... }
bool has = Components.Has<CurrentTransform>(entity);
```

`Get<T>`/`TryGet<T>` map `T` to its component name and call `World.TryRead<T>`. Because the struct layout is the
engine's, the bytes line up exactly.

## 5. Verified (lab, server svn 1285601)
The probe dumped 9 component types with real offsets (e.g. `NetworkHealth {health@0, healthMax@4}`,
`CurrentTransform` 56 B = `keen::WorldTransform`, `TeleportCount {counter u8 @0, lastTeleportTime keen::Time @8}`,
`NpcCommon` 1688 B / 36 fields). `Components.Get<T>` was cross-checked byte-for-byte against the raw component bytes
on live entities: **CurrentTransform matched on 2702 entities, TeleportCount on 6** — 0 differences.

## Caveats
- The dump is per build: regenerate `sigdb/types/<side>/<sha>.json` and `Generated.cs` for a new exe. (A future step
  could resolve the record layout itself through sigdb so the probe needs no code change across builds.)
- Non-primitive fields are raw byte buffers; add friendly nested structs (float3, WorldTransform, Time) by hand or
  extend `typegen.py` if a mod wants them typed.
- No GPL code is used (kfc-parser is never read); the layouts come from the game's own runtime reflection.
