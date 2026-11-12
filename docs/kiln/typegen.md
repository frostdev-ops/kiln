# typegen — stable bindings for the game's ECS types (Kiln.Game)

Forge gives mods stable names for Minecraft's fields. Kiln's equivalent is **Kiln.Game**: generated, explicit-layout
C# structs for the engine's ECS components, with type-hash constants and a typed read API, all derived from the
game's own reflection so the offsets are exact on the build they were generated for.

```
game reflection registry ──(walk)──► sigdb/types/<side>/<sha>.json ──(typegen.py)──► managed/Kiln.Game/Generated.cs
   content/TypeByHash              tools/typegen/probe                 tools/typegen/typegen.py
```

## 1. The reflection registry
The engine keeps a static reflection record (a `TypeMetadata`) for every `keen::` type. The hash of a type is
FNV-1a-32 of its qualified name (an ECS component is `fnv1a("keen::ecs::" + name)`, the same hash kiln.world uses).
The records are reachable from the exe file alone, with no game running: `native/services/types/scan.h`
(`find_type_table`) finds them by data, and both the live `kiln.types` service and `typedump` (section 2) use it.

**The table.** A `.rdata` array of `TypeMetadata*`, found because the records of `BlobString` and `uint32` sit next
to each other at its start. The qword that points at the start begins a header:

| Header offset | Meaning |
|---|---|
| `+0x00` | `TypeMetadata**` the table; `+0x08` u64 entry count |
| `+0x10` | `{ptr,len}` the name `"ecs.component"`; `+0x20` the component list (`TypeMetadata**`); `+0x28` its count |
| `+0x30` | `{ptr,len}` the name `"ecs.event"`; `+0x40` the event list; `+0x48` its count |

`find_type_table` checks that every entry is a record whose hash equals FNV-1a of its qualified name, and that every
list entry is a table entry.

**A record** (`scan.h`, namespace `ktypes::td`):

| Offset | Meaning |
|---|---|
| `+0x00` | name `{ptr,len}` |
| `+0x20` | qualified name `{ptr,len}` |
| `+0x38` | inner `TypeMetadata*`: base class (struct), element (static array), target (typedef), underlying integer (enum), the enum (bitmask) |
| `+0x40` u32 | size; `+0x44` u16 alignment |
| `+0x48` u32 | field count (struct) or value count (enum) |
| `+0x4C` u8 | kind; `+0x4D` u8 flags (never dumped) |
| `+0x50` u32 | FNV-1a-32 of the qualified name |
| `+0x58` | fields: stride `0x30`, name `{ptr,len}`, type `TypeMetadata*` `+0x10`, offset u64 `+0x18` |
| `+0x60` | enum values: stride `0x28`, name `{ptr,len}`, value u64 `+0x10` |
| `+0x70` | default value `{ptr,len}` (never read by the dump tool, never dumped) |

`ktypes::field_entry` and `ktypes::enum_entry` validate each entry (a field must have a name, a record as its type and
end inside its struct).

Kinds: 1 bool, 2 u8, 3 s8, 4 u16, 5 s16, 6 u32, 7 s32, 8 u64, 9 s64, 10 f32, 11 f64, 12 enum, 13 to 16 bitmask (1, 2,
4, 8 bytes), 17 typedef, 18 struct, 19 static array, 20 to 23 ds array/string/optional/variant, 24 to 27 blob
array/string/optional/variant, 28 object reference, 29 guid.

## 2. Dumping it (typedump)
`tools/typegen/typedump` is a host C++ tool over `scan.h`. It reads an exe **file** with a read-only stream (the exe
stays where it is installed and is never copied), lays out its sections in memory, finds the table and writes
`<out>/<side>/<sha256>.json`, the SHA-256 of the exe computed by the tool. It replaces the lab probe
(`tools/typegen/probe`) for dumping.

```sh
KILN_OUT=<build dir> tools/typegen/typedump/build.sh          # prints $KILN_OUT/typedump/typedump
$KILN_OUT/typedump/typedump --side server --out sigdb/types "<path to enshrouded_server.exe>"
$KILN_OUT/typedump/typedump --side client --out sigdb/types "<path to enshrouded.exe>"
```

The exe path is only ever a quoted argument of the tool; it is never run. On a Linux PC with Wine registered for
`.exe` files, a shell line that begins with that path starts the game.

**What is dumped:** the inline closure of the two ECS lists. From a type in it the tool follows a struct's base class
and every field's type, a static array's element, a typedef's target, an enum's underlying integer and a bitmask's
enum. Kinds 1 to 11 are leaves; so are kinds 20 to 29 (ds and blob containers, object references, guids): they are
written with name, hash, kind, size and alignment and nothing they point at is followed. A record that fails
validation, a pointer that leads outside the table, or two records with the same qualified name fail the dump: there
are no silent holes. Same exe, same bytes.

**Schema 2:**

```
top:    schema (2), side ("server" | "client"), exe_sha256, components [qualified names, sorted],
        events [qualified names, sorted], types [sorted by qualifiedName]
type:   name, qualifiedName, hash ("0x%08X"), kind, size, align,
        inner (kinds 12..19, a struct only when it has a base), fields (kind 18),
        values (kind 12; kinds 13..16 only when they list values)
field:  name, offset, type (a qualifiedName in this file)
value:  name, value (integer)
```

Keys are written in this order, indent 1, `\n` line ends, a final newline. An enum value is written as its underlying
integer type has it (found through typedefs): sign-extended from the enum's size for a signed type, masked to that
size for an unsigned one; an enum over anything but an integer fails the dump. `sigdb/types/server/c5c9fe50….json` is
the older probe dump (schema 1, nine components) and stays as it is: it is the golden input of the dev.24 generator.

**What is never dumped, and why.** The owner allowed committing these files on one condition: structure only (type
and field names, kinds, sizes, alignments, offsets, hashes, enum values). A record also points at the bytes of its
type's default value (`+0x70`). Those are game data, and a byte of it that reaches the repository cannot be taken
back, so `typedump` has no code path that reads that pointer, and it writes no flags byte, address, RVA, image base,
table index or table count. Names are refused unless they are plain C++ type or member names.
`managed/tests/Kiln.Game.Tests/DumpAllowListTests.cs` is the gate: it walks every file under `sigdb/types/` and
fails on any key outside its schema's allowed set, a value of the wrong shape (a name that is not a name or looks
like hex data, a hash that is not FNV-1a of the qualified name, a non-integer where an integer belongs, a nested
object), an `inner` or field type that does not resolve, a list entry that is not a type, or a field that ends past
its struct. Fixtures under `Fixtures/dump/` (made-up types) prove that each of these is rejected.

**Committed dumps** (game svn 1287369): server `f0d824be…aeb2` and client `5d9ef2f1…0c77`, each 687 components, 132
events, 1,839 types, 4,468 fields, 1,767 enum values. On this build pair the two files differ only in `side` and
`exe_sha256`.

**Adding a new build's dump:**
1. Record the build in `sigdb/builds/<side>/` as usual and check the exe's SHA-256 against it (`sha256sum`).
2. Run `typedump` on the installed exe (above), with `--out sigdb/types`.
3. Run the gate and the tool's test, which also dumps the exes again and compares with the committed files byte for
   byte:
   ```sh
   dotnet test managed/tests/Kiln.Game.Tests
   export KILN_SERVER_EXE="<path to enshrouded_server.exe>"
   export KILN_CLIENT_EXE="<path to enshrouded.exe>"
   KILN_OUT=<build dir> tools/typegen/typedump/test.sh
   ```
4. Commit the new `sigdb/types/<side>/<sha256>.json` alone. Never edit a dump by hand.

Without the exe variables `test.sh` runs only its synthetic image (`synthetic.bin`, mode 644): a small registry with a
struct and its base class, an enum whose stored values carry high garbage bits, a signed enum with negative values
behind a typedef, a bitmask, a static array, a typedef, a blob leaf and default-value bytes, whose dump must equal
`expected-synthetic.json` and must not contain those bytes. A copy whose signed enum sits over a float must fail.

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
