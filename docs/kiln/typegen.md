# typegen — stable bindings for the game's ECS types (Kiln.Game)

Forge gives mods stable names for Minecraft's fields. Kiln's equivalent is **Kiln.Game**: generated, explicit-layout
C# structs for the engine's ECS components, with type-hash constants and a typed read API, all derived from the
game's own reflection so the offsets are exact on the build they were generated for.

```
exe file ──(typedump, §2)──► sigdb/types/<side>/<sha256>.json ──(typegen.py at build time, §3)──► Kiln.Game.dll
                             committed, structure only            Kiln.Ecs.*, Kiln.GameTypes.*, the layout table
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
`managed/Kiln.Game` has no committed game types. Every build of `Kiln.Game.csproj` runs the generator on one schema-2
dump before the compiler and compiles what it writes:

```sh
python3 tools/typegen/typegen.py generate --dump sigdb/types/server/<sha>.json --out <dir> --root Ecs --legacy-obsolete
```

It writes four files into `<dir>` and prints one summary line. On the committed server dump:

```
typegen: f0d824be….json (server): 1839 types: 1201 structs, 227 enums, 68 bitmasks, 133 typedefs, 116 static arrays,
9 primitives, 85 leaves; 687 components, 132 events; 5171 fields (703 from bases), 198 wrappers, 26 inline-array
lengths; 37 raw buffers (36 a blob array (its elements live outside the struct); 1 a blob string (…))
```

| File | What |
|---|---|
| `Kiln.Game.Ecs.g.cs` | every struct, enum and bitmask of the dump, nested in `public static partial class Kiln.Ecs` |
| `Kiln.Game.Arrays.g.cs` | one `Ecs.StaticArray<N><T>` inline array per array length in use |
| `Kiln.Game.Legacy.g.cs` | the nine dev.24 types of namespace `Kiln.GameTypes`, by the v1 emitter (§3.8) |
| `Kiln.Game.layouts` | the layout table, embedded in the dll as the resource `Kiln.Game.layouts` (§3.6) |

Every one of them starts with the line
`// kiln-typegen: generated at build time from a game layout dump. Never commit this file.`
A test (`GeneratorTests.No_generated_file_is_among_the_sources`) fails when a file that starts with it is found under
`managed/`, `tools/` or `sigdb/` outside `obj/`, `bin/` and the made-up expected outputs in
`managed/tests/Kiln.Game.Tests/Fixtures/`.

Before it writes anything the generator checks the dump and stops with `typegen: error: <where>: <what>` on: a key
outside schema 2 (§2) at any level, a missing key, a value of the wrong shape, a hash that is not FNV-1a-32 of the
qualified name, a kind outside 1..29, an `inner` or field type or list entry that is not in the file, a base that is
not a struct or a base chain that loops, a field that ends past its struct, two fields that overlap, a field that
lies inside a non-empty base, a field name that repeats along a base chain, an enum value that does not fit its
underlying type, a bitmask whose enum has a value outside its bit width, a static array whose size is not its
length times its element, and any name collision (below). It emits nothing that is not derived from the allowed keys.

### 3.1 Names
All new types are nested in one class, `Kiln.Ecs` (the generator's `--root`, set once in `Kiln.Game.csproj` as
`KilnGameRoot`). 25 game short names equal public Kiln types (`Health`, `Fuel`, `EntityId`, `Time`, …), so they cannot
sit in a namespace a mod imports next to `using Kiln;`.

| Game name | C# name |
|---|---|
| `keen::ecs::X`, `keen::X` | `Ecs.X` |
| `keen::<ns>::X` | `Ecs.<Ns>_X`, `<Ns>` the namespace in PascalCase: `keen::enemy01::State` is `Ecs.Enemy01_State`, `keen::three_c::X` is `Ecs.ThreeC_X` (deeper namespaces: every part, joined) |
| a bitmask over enum `E` | `Ecs.<E's C# name>Mask` |
| a field or enum value that is a C# keyword | `@name` (`@params`) |

The generator stops, naming both game types, when two types map to one C# name. It also stops when a type would
take a name the root class uses for itself: the root's own name, `StaticArray<N>`, and the hand-written or reserved
members of `Ecs` (`ObjectReference`, `GameGuid`, and for typed access `Get`, `TryGet`, `Has`, `All`, `Available`,
`NameOf`, `Find`, `Read`, `TryRead`, `Write`, `TryWrite`, `Types`, `Layouts`; `ROOT_RESERVED` in `typegen.py`, to be
extended with every hand-written member of `Ecs`). A struct field named `Hash`, `Name`, `QualifiedName`, `Size` or
`Layout`, or named like its struct, stops it too.

The files that name the root class besides the csproj: `managed/Kiln.Game/Ecs.cs` (the hand-written part), and the
tests `RoundTrip.cs` (`RoundTrip.Root`), `RoundTripTests.cs` and `GameLayoutStampsTests.cs` (type names in expected
values), plus `tools/typegen/tests/run.py` and the expected outputs under `Fixtures/gen/expected/`.

### 3.2 Types
| Game kind | C# |
|---|---|
| 1 bool | `bool` |
| 2..9 integers, 10 float32, 11 float64 | `byte`, `sbyte`, `ushort`, `short`, `uint`, `int`, `ulong`, `long`, `float`, `double` |
| 12 enum | `public enum Ecs.X : <underlying>` (the underlying integer found through typedefs), the dump's names and values |
| 13..16 Bitmask8..64 | `[Flags] public enum Ecs.EMask : byte/ushort/uint/ulong`, one member per value `v` of the enum `E`, `= 1 << v` |
| 17 typedef | the target's C# type (the chain of typedef names is in the field's comment) |
| 18 struct | the nested generated struct |
| 19 StaticArray | `Ecs.StaticArray<N><T>`: `[InlineArray(N)]`, one generic struct per length; size `N ×` the element's |
| 28 object reference | `Ecs.ObjectReference` (hand-written, 16 bytes; the field's comment names the referenced type) |
| 29 guid | `Ecs.GameGuid` (hand-written, 16 bytes) |
| 24 BlobArray, 25 BlobString, 20..27 other blob and ds kinds, an array of any of them | `fixed byte name[size]`, with a comment that says why |

Raw buffers on this build: 37 fields, 36 blob arrays and one blob string (their data lives outside the struct behind a
self-relative offset). They are counted in the summary line by reason.

**Bitmask values are bit numbers.** Bit `v` of a bitmask stands for the enum value `v`. Kiln already reads game
bitmasks this way: `native/services/content/refl.cpp` (a kind 13..16 field becomes `KILN_CONTENT_FLAGS` over its
flag enum), `native/services/content/packs.cpp` (a flag name is written as `1ll << value` and tested as
`(v >> value) & 1`), `native/services/players/playerstate.cpp` ("a bitmask's enum holds bit numbers"), and
`docs/re/models.md` §2 ("Bitmask values are bit indices of the flag enum (155 of 155 bitmask enums)", confirmed by
re-encoding every resource of the client). `docs/re/impact.md` (`usedStreams`, `Bitmask8<EventStreamType>`) is one
such field. On this build every bitmask's enum has values `0..n-1` below the bit width; the generator checks it.

### 3.3 A generated struct
```csharp
/// <summary><c>keen::ecs::NetworkHealth</c>: an ECS component, 8 bytes, align 4. Base <c>keen::ecs::Component</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public partial struct NetworkHealth : IGameComponent<NetworkHealth>
{
    public static readonly uint Hash = 0x216EB5F9u;          // FNV-1a-32 of QualifiedName
    public static readonly string Name = "NetworkHealth";    // the game's short name, the one kiln.world uses
    public static readonly string QualifiedName = "keen::ecs::NetworkHealth";
    public static readonly int Size = 8;
    public static readonly ulong Layout = 0x…ul;             // §3.5
    static uint IGameStruct<NetworkHealth>.Hash => Hash;     // … and the other four
    [FieldOffset(0)] public uint health;
    [FieldOffset(4)] public uint healthMax;
}
```
- `static readonly`, never `const`: a constant is copied into every mod that reads it and would not follow the game.
- A base class's fields are flattened into the derived struct, base first, at their own offsets. An empty base (the
  1-byte `keen::ecs::Component`, `Event`) takes no room. On this build the base of the attribute components,
  `keen::ecs::AttributeComponent`, is reflected with four fields (`rootId`, `storageOffset`, `flags`,
  `storageCount`), so `Ecs.Health` has them at 0..7 and `dataStorage` at 8. Nothing is named from `docs/re/`.
- A struct whose only field sits at offset 0 and fills it (`EntityId {id}`, `Time {value}`, `HashKey32 {value}`: 198
  on this build) gets implicit conversions to and from that field's type.
- Every struct implements `IGameStruct<TSelf>` (top-level in namespace `Kiln`, hand-written: static abstract `Hash`,
  `Name`, `QualifiedName`, `Size`, `Layout`), the dump's components `IGameComponent<TSelf>`, its events
  `IGameEvent<TSelf>`. The interfaces name no Kiln.Game type, so they can move to Kiln.Api by type forwarding.
- Structs are `partial`, so a hand-written part (typed access, attribute views) can add members.
- Bytes the reflection does not name (padding, unreflected gaps) have no field.

### 3.4 The public API record
`Kiln.Game` is gated by the PublicApiAnalyzers like every mod-facing project. `PublicAPI.Shipped.txt` holds the nine
dev.24 types, `ComponentTypes` and `Components` (never edited); `PublicAPI.Unshipped.txt` the hand-written additions
(`Ecs`, `Ecs.ObjectReference`, `Ecs.GameGuid`, the three interfaces, `GameLayoutStamps`). The generated `Ecs.*`
types follow the game and are outside the record: each generated file starts with `#nullable enable` and
`#pragma warning disable RS0016`, and no generated member is in a PublicAPI file. A test fails when a hand-written file
of Kiln.Game names RS0016. What guards the generated surface is the round trip (§3.9), the layout stamps and the
first-use check.

### 3.5 The layout hash
`Layout` is FNV-1a-64 (offset `0xcbf29ce484222325`, prime `0x100000001b3`) over the UTF-8 of a type's canonical
text. Each line ends with `\n`; `<h>` is the named type's own layout hash as 16 lower-case hex digits:

| Kind | Text |
|---|---|
| primitive | `primitive <qn>` `kind <k>` `size <n>` |
| enum | `enum <qn>` `size <n>` `underlying <qn>`, then `value <name>=<value>` per value in dump order |
| bitmask | `bitmask <qn>` `size <n>` `enum <qn> <h>` |
| typedef | `typedef <qn>` `target <qn> <h>` |
| static array | `array <qn>` `length <n>` `element <qn> <h>` |
| struct | `struct <qn>` `size <n>` `align <n>` `base <qn> <h>` (or `base -`), then `field <name> <offset> <type qn> <h>` per own field |
| other (leaves) | `leaf <qn>` `kind <k>` `size <n>` `align <n>` |

A change anywhere inside a type changes the hash of every type that contains it. `RoundTrip.Layout` in the tests
computes the same from the dump alone, without the generator.

### 3.6 The layout table (`Kiln.Game.layouts`)
UTF-8 text, `\n` line ends, fields separated by tabs. The marker line, then a header, then one line per generated
struct, enum and bitmask, sorted by C# name (ordinal):

```
kiln.game.layouts  1  side=server  exe_sha256=<64 hex>  root=Ecs  types=<n>  set=<16 hex>
S  Kiln.Ecs+NetworkHealth  <layout>  keen::ecs::NetworkHealth  8  component  health@0:uint  healthMax@4:uint
E  Kiln.Ecs+Enemy01_State  <layout>  keen::enemy01::State      1  byte       <name>=0  <name>=1  …
F  Kiln.Ecs+FactionTypeMask  <layout>  keen::Bitmask16<keen::FactionType>  2  ushort  <name>=1  <name>=2  …
```

A line is: kind (`S` struct, `E` enum, `F` bitmask), the C# full name as reflection writes it (`Type.FullName`),
the layout hash (16 lower-case hex), the qualified game name, the size, then for a struct its role (`component`,
`event`, `struct`) and each field as `<name>@<offset>:<C# type>`, for an enum or bitmask its underlying C# type and
each `<name>=<value>` (a bitmask's values are the mask values `1 << v`). The C# type is as written in the generated
source: a keyword for a primitive, `Ecs.<Name>` for a generated or hand-written type, `Ecs.StaticArray<N><T>` with
`<T>` written the same way, `fixed byte[<size>]` for a raw buffer. Names are the game's (no `@`). `set` is the
FNV-1a-64 of every line after the header, each with its `\n`. `GameLayoutStamps.Table` returns the table as embedded.

### 3.7 Layout stamps (`kiln.gametypes.json`) and `GameLayoutStamps.Check`
A mod binds to a generated type's fields by name when its code is compiled at run time, and enum constants are copied
into it when it is built. The SDK build task (the next typegen task) writes, next to the mod, a stamp of what it uses:

```json
{ "schema": 1, "assembly": "MyMod.dll", "mvid": "<guid>", "layoutSet": "<set of the Kiln.Game it was built against>",
  "types": { "Kiln.Ecs+Fuel": { "layout": "<hex>", "fields": { "dataStorage": "Ecs.StaticArray8<int>" } },
             "Kiln.Ecs+Enemy01_State": { "layout": "<hex>", "values": { "Idle": 0 } } } }
```

`fields` lists the fields of the type that the mod uses, each with its C# type in the table's form (the stamp task
can copy it from the table of the Kiln.Game the mod is built against); `values` lists every value of every enum or
bitmask the mod names, used or not. `GameLayoutStamps.Check(stampJson, out reason)` (any thread) compares it
with this assembly's table:
- `schema` other than 1, unreadable JSON, not an object: refused, with that reason.
- `layoutSet` equal to the table's `set`: fine.
- a stamped type this assembly lacks: refused, naming the type.
- equal layout hash: fine. Different: every stamped field must exist with the same C# type, every stamped enum
  value must exist with the same number; otherwise refused, naming the type, the field or value, and what it changed
  from and to. A type whose layout changed while what the mod uses did not is fine (new fields, new enum values,
  moved fields).

Reasons look like `MyMod.dll: Kiln.Ecs+Fuel.dataStorage changed from Ecs.StaticArray8<int> to Ecs.StaticArray4<int>
in this Kiln.Game (server f0d824be)`.

### 3.8 The legacy namespace `Kiln.GameTypes`
The nine dev.24 types (`NetworkHealth`, `Health`, `Mana`, `Stamina`, `CurrentTransform`, `LastTransform`, `Velocity`,
`TeleportCount`, `NpcCommon`), `ComponentTypes` and `Components` stay with exactly their dev.24 members. They are made
per build by the v1 emitter from the build's dump (the nine types' own fields), so their offsets follow the game, in
the text of `managed/Kiln.Game/Generated.cs` plus the marker line, one `#pragma warning disable CS0618` and, with
`--legacy-obsolete` (what the csproj passes), `[System.Obsolete("Use Kiln.Ecs.<Name>.")]` on each struct.
`Generated.cs` stays in the tree, byte for byte, as the golden copy (and as the input of the Rust port's test in
`crates/kiln-manage`); it is not compiled. The two-argument command line is the v1 generator, unchanged:

```sh
python3 tools/typegen/typegen.py sigdb/types/server/c5c9fe50….json managed/Kiln.Game/Generated.cs   # same bytes
```

`using Kiln.GameTypes;` does not compile in a mod: the namespace in Kiln.Game clashes with the class `Kiln.GameTypes`
in Kiln.Api (CS0434). The old types can only be named through an extern alias:

```xml
<Reference Include="Kiln.Game" Aliases="game" />   <!-- or a ProjectReference with Aliases="game" -->
```
```csharp
extern alias game;
using G = game::Kiln.GameTypes;
```

That is why the new types are in `Kiln.Ecs`. A game field that disappears from one of the nine types fails the build
with RS0017; a new one with RS0016 (it would become dev.24 API: decide, do not just record it).

### 3.9 The build
`Kiln.Game.csproj` runs the generator in the target `KilnGenerateGameTypes`, before `PrepareResources` and
`CoreCompile`, into `$(IntermediateOutputPath)kiln-typegen/`: with `--artifacts-path` (every Kiln build passes one)
that is under the build output, outside the tree; without one it is the project's git-ignored `obj/`, the only place
in the tree a generated file may be. The target is incremental (inputs: the dump, `typegen.py`, the csproj; outputs:
the four files) and the files are in `FileWrites`. A second build with nothing changed does not run the generator;
touching the dump does.

| Property | Default | |
|---|---|---|
| `KilnGameDump` | `sigdb/types/server/f0d824be….json` | the dump; the newest server dump, pinned by path: one line for a new build |
| `KilnPython` | `python3` | the interpreter (3.8 or newer) |
| `KilnGameVariant` | (none) | a short name; the variant gets its own intermediate and output folders (`<config>_<variant>` under the artifacts path), so several dumps build side by side |
| `KilnLocalGameTypes` | (none) | a file that replaces the legacy part (the generator then runs with `--no-legacy`) |

Without python3 the build stops before the compiler with one line: `Kiln.Game needs python3 (3.8 or newer) to generate
its game types, and 'python3' did not run it: install python3 or pass -p:KilnPython=<interpreter>.` A generator that
refuses its dump stops the build with its own `typegen: error: <where>: <what>` line, followed by one line naming
the dump. The project references to Kiln.Api and
Kiln.World carry `GlobalPropertiesToRemove="KilnGameDump;KilnGameVariant;KilnPython;KilnLocalGameTypes"`, so they
are built once for every variant.

Building against a local dump (for example a new build's, before it is committed):

```sh
dotnet build managed/Kiln.Game -c Release --artifacts-path $KILN_OUT/dotnet \
  -p:KilnGameDump=/path/to/<sha256>.json -p:KilnGameVariant=local
# $KILN_OUT/dotnet/bin/Kiln.Game/release_local/Kiln.Game.dll
```

### 3.10 Tests (no game, no lab)
- `python3 tools/typegen/tests/run.py` (also run by `GeneratorTests`): the made-up fixture dump
  `Fixtures/gen/lanterns.json` (written by `tools/typegen/tests/mkfixture.py`; every kind and rule above) against the
  checked-in output in `Fixtures/gen/expected/` (`run.py --bless` rewrites it), every refusal, the root option,
  enum literals at the edges, and the legacy mode against `Generated.cs`.
- `LegacyGoldenTests`, `LegacyBinaryTests`: the nine dev.24 types by reflection against a table derived from dev.24's
  `Generated.cs` and `Components.cs`, and a consumer compiled against the dev.24 shape that runs against the built
  Kiln.Game (and fails to bind against a variant with one field renamed).
- `RoundTripTests`: the built Kiln.Game against every schema-2 dump under `sigdb/types/` (sizes by
  `Unsafe.SizeOf`, every field offset bases included, every field type's size, hashes, names, layout hashes, enum
  names, values and underlying types, mask values, inline-array lengths and element sizes, the marker interfaces
  against the lists, the number of nested types); the server and client dumps' types are equal; the fixture variant
  against its dump, and a doctored copy that must fail; the legacy emitter on the old and the new dump against the
  golden.
- `GameLayoutStampsTests`: `Check` against hand-made tables and stamps, and the embedded table.

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
- The dump is per build. A new game build is a new dump (§2) and one line in `Kiln.Game.csproj` (`KilnGameDump`);
  nothing generated is committed, so there is nothing else to regenerate. `Generated.cs` is the golden copy of the
  dev.24 legacy part and is never regenerated.
- Building Kiln.Game needs python3 (3.8 or newer). Without it the build stops with one line saying so (§3.9).
- The generated types follow the game. A game update can drop or retype a field: the public API record does not hold
  them (only the nine legacy types and the hand-written members are recorded); the layout stamps and
  `GameLayoutStamps.Check` (§3.7) do.
- Blob arrays and blob strings stay raw bytes: their data lives outside the struct, behind a self-relative offset that
  only means something in the game's memory.
- The base of the attribute components (`keen::ecs::AttributeComponent`) is reflected on this build and flattened like
  any base. What the slots of an attribute's `dataStorage` mean is not in the reflection; a view over them is left to
  the typed access work.
- No GPL code is used (kfc-parser is never read); the layouts come from the game's own reflection table.
