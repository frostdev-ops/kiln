# Impact programs: format and VM semantics (svn 1285601)

Enshrouded runs every gameplay effect through "Impact" programs: spells, weapon attacks, perks, buffs, skill nodes,
enemy attacks. A program is u32 bytecode for a 30-opcode stack VM that calls C++ natives. This document is the
evidence behind `tools/impact` (Kiln's MIT toolchain, docs/kiln/modding/impact.md). docs/re/abilities.md covers how
programs are bound (templates, items, sequences, buffs, perks, skill nodes) and the config values they get.

Builds: dedicated server Steam 26404334 / svn 1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`), client Steam
26404966 (`enshrouded.exe` sha256 `ce6fdba6…`). RVAs are server unless marked "client".

Sources:
- the game's reflection records, read offline with `tools/kfc`;
- the 571 programs the client ships (`tools/impact roundtrip` reads them all);
- Ghidra 12.1.2 headless decompiles (no auto-analysis) and capstone, of the interpreter `0x990920`, `runImpact`
  `0x99c1e0`, `createImpactInstance` `0x993eb0`, `registerNative` `0x996b80`, and the 100 native functions;
- docs/re/abilities.md (abilities-research).

From kfc-parser (GPL-3) only one documented fact was used, by abilities-research: a program has code, shutdown code
and data. Its code was not read.

Tags (all but `[hyp]` count as proven):
- **[dis]** disassembly or decompile;
- **[refl]** game reflection;
- **[data]** all 571 vanilla programs (`impact.py roundtrip`);
- **[rt]** lab runtime (docs/re/abilities.md);
- **[hyp]** hypothesis.

## 1. The resource
`keen::ImpactProgram`: type hash `0xd4922d88`, layout hash `0x4daf7389`, 64 bytes, align 4. The 571 programs are
listed by the one `ImpactRegistryResource` (`c4933fae-…`). **[refl] [data]**

| Offset | Field | Type | Notes |
|---|---|---|---|
| +0x00 | `id` | ProgramId (HashKey32) | = FNV-1a-32 of the GUID's 16 bytes in all 571 **[data]**; `impact_initialize` computes the same when a component's id is 0 **[dis]** |
| +0x04 | `programGuid` | Guid | = the resource GUID in all 571 **[data]** |
| +0x14 | `stackSize` | u16 | 256 in all 571 **[data]**. `createImpactInstance` ignores it (§3) **[dis]** |
| +0x16 | `usedStreams` | Bitmask8\<EventStreamType\> | OnCollision 0, OnHit 1, OnParry 2, EntityInRange 3. Vanilla: 0 (446 programs), OnCollision (124), OnHit (1) **[refl] [data]** |
| +0x18 | `code` | BlobArray\<ImpactCommand = u32\> | the main program |
| +0x20 | `codeShutdown` | BlobArray\<u32\> | runs when the instance ends; non-empty in all 571, and every one ends in HALT **[data]** |
| +0x28 | `dataLayout` | BlobArray\<ImpactVariable\> | the variables |
| +0x30 | `data` | BlobArray\<u8\> | the initial instance data. Its length is exactly max(offset + size) in all 571, and no byte outside a variable is non-zero **[data]** |
| +0x38 | `queryCallIds` | BlobArray\<u32\> | the ECALL site ids of the query natives (§5) **[data] [dis]** |

`keen::ImpactVariable` (24 B, layout `0xdcf25e05`) **[refl]**:

| Offset | Field | Notes |
|---|---|---|
| +0x00 | `name` | HashKey32, an opaque id. It is not FNV(dbgName) for any of the 13,850 vanilla variables, and only 11,523 values are distinct **[data]**. COPY finds variables by it **[dis]**. A hash of the editor pin [hyp] |
| +0x04 | `configId` | 0 = not config-bound. 2,979 bound variables, 346 distinct ids (docs/re/abilities.md §1) **[data]** |
| +0x08 | `type` | FNV-1a-32 of the type's **impact name**: the slice at +0x10/+0x18 of its reflection record ("float32", "bool", "sint32", "Time", "ecs.EntityId", "impact.DamageSetup", "WorldTransform", …). All 49 vanilla types resolve this way. `KnowledgeIdContainer` is the exception: it uses its qualified-name hash **[refl] [data]** |
| +0x0C | `size` | u16, = the reflection size of the type **[data]** |
| +0x0E | `offsetInBytes` | u16 into the instance data. Struct fields are separate variables that alias their struct (`Damage1::value` 52 B at +364, `Damage1.Physical_Cut::value` 4 B at +364, …). Offsets are multiples of 4 and mostly of 16 **[data]** |
| +0x10 | `dbgName` | BlobString, "Node::pin" (`impact_nodes.GetTransform::transform`, `NoiseRange::value`). Unique within all but 3 programs **[data]** |

**Blob layout.** The root struct comes first. Then the out-of-line blocks follow in field order, each aligned to its
element type: code, codeShutdown, the variable block with all dbgName strings behind it (align 1), data (align 1),
queryCallIds (align 4). An empty array is `{0, 0}`. Offsets are self-relative.
- This is tools/kfc's generic blob rule. Both `tools/kfc` (generic reflected decode/encode) and `tools/impact`'s own
  codec re-encode all 571 programs byte for byte. **[data]**
- The editor-side `keen::ImpactInfo` (node GUIDs, `programFlow`, `ImpactFunctionCallDebugInfo{nodeName, nodeId, pc}`)
  is not shipped. **[refl] [data]**

## 2. The interpreter (`0x990920`, client `0x1239960`)
Signature: `interp(Instance* inst, Slice<u32>* code, ImpactProgram* prog, EcsIterCtx* ctx)`. `runImpact` passes
`program.code`. **[dis]**

**Instance state:**
- `+0x1EC` pc (u16, a word index);
- `+0x1EE` sp (u16: the index of the top slot; 0xFFFF = empty);
- `+0x1F0` fp (u16);
- `+0x1F2` flags (`keen::impact::Flags`: Initialized 0x01, Executed 0x02, CleanupDone 0x04, Halt 0x08, Error 0x10,
  TemporaryDisabled 0x20, Result_DestroyEntity 0x40, ShutdownSheduled 0x80);
- `+0x18` data, `+0x30` stack, `+0x38` stack slot count.

**[dis] [refl]**

**The loop.** The interpreter returns at once when Halt is set. Otherwise it executes until Halt, Error, a yield
(RVM) or a suspending ECALL, then stores pc/sp/fp.
- There is **no step limit**: a loop with no RVM and no native that ends it hangs the simulation step.
- There are **no bounds checks**:
  - on pc: running past the end reads beyond the array;
  - on pushes: they write slot (u16)(sp+1);
  - on most pops: only POP and ECALL check for an empty stack.
- After every run the stack canary must be intact: `stack[count-1] == 0xBAADF00D`, else a log line and a fatal
  `int3`.

**[dis]**

| # | Op | Operands | Effect (s = stack, top = s[sp]) | Tag |
|---|---|---|---|---|
| 1–4 | IADD ISUB IMUL IDIV | – | pop b, a; push a op b (32-bit wrap; IDIV signed, an x64 `idiv`: b = 0 faults the game) | [dis] |
| 5 / 6 / 7 | ILT / IEQ / ILEQ | – | signed `<`, `==`, signed `<=` → 1/0 | [dis] |
| 8 | BR | target | pc = target (u16, an absolute word index) | [dis] |
| 9 / 10 | BRT / BRF | target | pop c; jump if c ≠ 0 / c = 0 | [dis] |
| 11 | ICONST | value | push the word: a raw value, or a variable handle `0xFFFF0000 \| index` | [dis] |
| 12 / 13 | ICONST0 / ICONST1 | – | push 0 / 1 | [dis] |
| 14 / 15 | INC / DEC | – | top ± 1 | [dis] |
| 16 | COPY | src name, dst name | find the first variables whose `name` equals each operand (not found → push 0x1F, Error); types must match (else push 0x2C, Error); memmove the bytes | [dis] |
| 17 | DUP | – | push top | [dis] |
| 18 | CALL | target, n | push n, fp, pc+3; fp = sp (the return-pc slot); pc = target | [dis] |
| 19 | ECALL | site, hash | §4 | [dis] |
| 20 | RET | – | result = top; pc = s[fp]; fp' = s[fp−1]; n = s[fp−2]; sp = fp−2−n; s[sp] = result. The n values pushed before CALL become the result | [dis] |
| 21 / 23 | LOAD / STORE | k (signed) | push s[fp+k] / s[fp+k] = pop | [dis] |
| 22 | GLOAD | handle | push variable(handle & 0xFFFF), by type: uint32 `0x32940bec`, sint32 `0x9859f31e`, float32 `0xe89f7410` → the 32 bits; bool `0xc894953d` → byte ≠ 0; Time `0xdfe4e404` → i64 ns / 10⁶ (ms). Another type, or operand 0 → "Can not load Type" log, Error | [dis] |
| 24 | GSTORE | handle | the reverse; Time = (u32)ms × 10⁶ (zero-extended: negative ms do not survive) | [dis] |
| 25 | LTIME | – | push the ECS time in ms (ns / 10⁶) | [dis] |
| 26 | TIMEFF | – | top: float32 bits (seconds) → `(i64)(s × 10⁹) / 10⁶` ms (a `movss` reinterprets the slot; not an int → float conversion) | [dis] |
| 27 | POP | – | pop; on an empty stack: Error | [dis] |
| 28 | RVM | – | yield: pc+1 is stored, and the instance continues there next step | [dis] |
| 29 | DSELF | – | flag Result_DestroyEntity (0x40); **execution continues** | [dis] |
| 30 | HALT | – | flag Halt (0x08); stop | [dis] |
| 0, ≥ 31 | – | – | flag Error | [dis] |

Use in vanilla programs **[data]**:
- CALL, RET, LOAD, STORE and COPY: **0 uses** in 571 programs.
- Every ICONST operand is a variable handle: literals live in variables.
- The compiler's idioms:
  - wait: `gload $t; ltime; iadd; L: dup; ltime; ileq; brt done; rvm; br L; done: pop`;
  - for-each: `iconst0; gstore $index; L: ecall forEach…; brt body; …; body: …; br L`;
  - call statement: argc pushes, then `ecall`, then `pop` or `brt/brf`;
  - every `code` and `codeShutdown` ends in HALT; the trailing HALT of `code` is unreachable.

## 3. Instances (`createImpactInstance` `0x993eb0`, `runImpact` `0x99c1e0`)
- **Stack: 32 u32 slots.** The function allocates 0x80 bytes (align 4) and stores count 0x20 at `+0x38`. It writes
  the canary `0xBAADF00D` into slot 31 and sets sp = 0xFFFF. `stackSize` (+0x14) is not read.
  - Usable depth is therefore **31**.
  - A push at depth 31 overwrites the canary, and the run ends in a fatal error.
  - The vanilla maximum is 25 (applyDamage / forEachHitEvent pass 25 handles). **[dis] [data]**
- **Data:** `data.count` bytes (align 16), copied from `program.data`. Config values then overwrite the bound
  variables: once per instance, see docs/re/abilities.md §1. **[dis]**
- **Streams** by `usedStreams`: OnCollision a 0x80-entry buffer (+0xC0), OnHit 0x40 (+0x98), OnParry 0x10 (+0xE8).
  EntityInRange allocates nothing. **[dis]**
- **Queries:** a map at `+0x110` keyed by each `queryCallIds` entry, with 0x38-byte values that each hold a 32-entry
  array. **[dis]**
- Instance `+4` = the ProgramId: `runImpact` resolves the program through `getProgram(registry, inst+4)` on **every
  run**, so code is read live. **[dis]**
- **After a run** `runImpact` sets Executed. When Halt is set without DestroyEntity or TemporaryDisabled, it adds
  component `0xe3b96393` (ImpactHalted) or sets TemporaryDisabled. **[dis]**
- **Errors:** after an error (flag 0x10) the instance is dead. It is never interpreted again, while the entity lives
  on. **[dis]**
- **Shutdown:** when `codeShutdown` runs (instance end, entity destroy) follows EMF plumbing §5.2. Not re-checked here
  **[hyp]**.

## 4. Natives
**Registration** **[dis]**:
- `bool registerNative(registry, u32 hash, fn, Slice<char>* displayName, u8 argc)` (`0x996b80`, client `0x123fbc0`
  on these older builds). Corrected 2026-11 (dev.28 RE): success is returned in AL, not void; argc is the fifth
  argument on the stack. Current-build addresses and call evidence are in §9 below. [static] [lab]
- Called 108 times from the world setup (`0x233ee0`, sites `0x23650a..0x2381ce`), with the **same 108 (hash, argc,
  name) on server and client** (`tools/impact` `scan_natives`, found through the `applyDamage` name string).
- The registry is a hash map at `registry+0x20`:
  - bits `+0x28`, capacity `+0x30` (256);
  - keys `+0x48`; 0x50-byte values `+0x60` (`+0` fn, `+8` display name slice into an inline 32-character buffer,
    `+0x48` argc);
  - count `+0x74`;
  - linear probing from `hash & (cap−1)`.
- An existing key is overwritten: `ChangeAttribute` is registered twice, so 107 keys **[dis] [rt]**.
- A name longer than 32 bytes traps (`int3`) **[dis]** (abilities-runtime).

**The hash is FNV-1a-32 of the node name, not of the display name** **[dis] [data]**:
- `FNV("ApplyDamage")` = `0xb04d0e7a` (display name 'applyDamage').
- `0x798388df` = "ApplyChange" (displayed 'applyAttributeValue'), `0xe41652a7` "RevertChange", `0x044e4187`
  "ChangeAttribute", "AddValues"/"SubValues"/"MulValues"/"DivValues" ('add'…), "ClampValues", "IsEqual", "IsLess",
  "HasBuffNode", "CallFactionForHelp", "RevertAddSkillLevel".
- All 107 hashes resolve; the node names are strings in the exe.

**ECALL site, hash** **[dis]**:
1. Look up `hash` in the registry (via `EcsIterCtx+0x110`). Not found → push 0x1F, Error.
2. `argc ≥ 32` → log, Error.
3. Pop argc values into `args[0..argc−1]`: **args[0] is the top of the stack, the value pushed last**. Underflow →
   0x12, Error.
4. fn null (the three debugDraw natives) → push 1.
5. Else `fn(Result* out, Slice<u32>* args, NativeCtx* ctx)`. `out+0` is a `keen::ErrorId` byte and `out+8` the
   value byte:
   - 0 Ok → push the value byte;
   - 1/2/3 (Temporary_TimeOut / Temporary_OutOfMemory / Temporary_StillRunning) → **suspend**: pc and sp stay, the
     arguments stay on the stack, and the same ECALL runs again next step;
   - anything else → push the code, Error.

**Native context** (on the interpreter's stack) **[dis]**:

| Offset | Field |
|---|---|
| +0 | dataLayout pointer |
| +8 | its count |
| +0x10 | instance data |
| +0x18 | instance `+0x20` |
| +0x30 | EcsIterCtx |
| +0x38 | an RNG seeded with FNV-64 of (time in ms, entity id) |
| +0x40 | instance id |
| +0x44 | program GUID |
| +0x54 | ProgramId |
| +0x58 | **the call-site id** (ECALL's first operand) |

**Arguments.**
- Natives read each argument as `dataLayout[arg & 0xFFFF].offsetInBytes + data`. **They never check the 0xFFFF0000
  tag**: a raw integer n reads variable n.
- 0 means "not connected": a required input returns 0x18 NoConnection; an optional output is skipped.
- Typed reads (`0x993020`) take the variable's type, so the math and compare natives work on float32, sint32, Time,
  float3, ….
- **[dis]** (onEvent, triggerNoise, getSelf, getBuffTarget, the math natives, changeAttributeValue,
  forEachEntityWithFactionInRange, destroyEntity read in full; the result codes of all 100 functions scanned).

**Result codes seen in the 100 native functions** **[dis] [refl]**: 0 Ok, 1 Temporary_TimeOut (destroyEntity: suspend
and retry), 6 Canceled (terraforming/addWater), 0x11 Internal, 0x16 InvalidValue, 0x18 NoConnection, 0x1F NotFound,
0x2C TypeMismatch, 0x47 OutOfRange. The values are `keen::ErrorId` (`ErrorId` reflection enum).

**Call-site ids** **[data] [dis]**:
- 5,152 ECALLs carry 4,729 distinct site ids, **none shared between programs**.
- Within a program, an apply/revert pair shares its site (code and shutdown): applyAttributeValue/
  revertApplyAttributeValue, addSkillLevel/revertaddSkillLevel, forEachEntityWithFactionInRange/destroyFactionQuery.
- So the site id is the editor node's id, and natives key per-node instance state on it (ctx+0x58).

**Signatures.** `tools/impact/data/natives.json` lists every native in args[] order:
- argc **[dis]**;
- per argument, the variable types and pin names vanilla passes, how often unconnected, and the direction inferred
  from data flow ("out" = written there first and read by later instructions) **[data]**;
- what the result feeds (POP, or BRT/BRF for a boolean) **[data]**;
- hand-written semantics in `natives-notes.json`, tagged `dis`/`data`/`hyp` per native.

`impact.py natives --show <name>` prints one. The ones read in full: onEvent (args[0] eventStream out {callId 0,
type}, args[1] eventType), triggerNoise (args[0] position float3, args[1] range float32; noise event `0xd079d101`),
getSelf, getBuffTarget, add/sub/mul/div (one function; the op variable decides: result, op, a, b),
changeAttributeValue (attribute, op, scratch, entity, attributeOverride, value; Health `0x8eb84995`, Stamina
`0x04b6aa8b` and Mana `0x6aa2574e` changes are also reported), forEachEntityWithFactionInRange (entity, transform,
index, factions, origin, range; the query lives per site and is released at the end), destroyEntity (suspends while
the destroy fails).

## 5. queryCallIds
- In all 571 programs, `queryCallIds` is exactly the list of ECALL sites (in code order, code then shutdown) that call
  forEachEntityWithFactionInRange `0x19143416`, destroyFactionQuery `0x93e40aa9`, forEachPlayerInRange `0xa8787e70`,
  destroyPlayerQuery `0x22a62ea4` or entityInRangeWithFaction `0x61946152`. **[data]**
- startQuery/fetchQueryResult use QueryId variables instead and are not listed. **[data]**
- createImpactInstance preallocates per-site state only for the listed ids **[dis]**. A query native at an unlisted
  site has no state: the effect is unknown, so the verifier refuses it **[hyp]**.

## 6. Verifier rules and why
`tools/impact/impact/lint.py`; kiln.abilities applies the [rt] subset before it registers a program
(docs/kiln/services/abilities.md).
- **E_ID:** programs are found by id = FNV(GUID). **[dis] [data]**
- **E_SIZE:** code and shutdown are at most 65,535 words (pc, sp and targets are u16). **[dis]**
- **E_OPCODE, E_TRUNC:** an unknown opcode errors the instance; a truncated operand reads past the array. **[dis]**
- **E_TARGET:** a target must be an instruction start in the same array. **[dis]**
- **E_FALLOFF:** no path may run past the end; there is no bounds check. **[dis]**
- **E_END:** each array ends in HALT. Every vanilla array does **[data]**; kiln.abilities requires it.
- **E_DEPTH, E_UNDERFLOW, E_OVERFLOW:** a worklist over the control flow, from pc 0 at depth 0 in each array. Depth is
  the same on every path, no pop below 0, at most 31 after each push. Effects:
  - IADD..ILEQ 2→1; BRT/BRF 1→0; ICONST*/DUP/LTIME/GLOAD +1; INC/DEC/TIMEFF 1→1; GSTORE/POP −1;
  - ECALL −argc +1 (argc from the registered native);
  - BR, COPY, RVM, DSELF, HALT 0.
  - Successors: BR → target; BRT/BRF → target, next; HALT → none; RVM and DSELF → next.
- **E_FRAME:** CALL/RET/LOAD/STORE are refused. Vanilla never uses them, and their frame layout is only known from
  the decompile.
- **E_HANDLE, E_LOADTYPE:** GLOAD/GSTORE need a tagged handle below the variable count, of a loadable type. **[dis]**
- **E_COPY:** both names exist, same type. **[dis]**
- **E_NATIVE:** the hash is vanilla, a Kiln built-in or declared by the mod (argc from that declaration).
- **E_QUERY:** §5.
- **E_ARG:** a raw integer (ICONST n, ICONST1) where a native reads a handle. It would read variable n. Allowed only
  for Kiln params declared `"raw": true`.
- **Warnings:**
  - W_LOOP: a loop with no RVM and no ECALL (the interpreter has no step limit);
  - W_STREAM: onEvent on a stream that `usedStreams` does not allocate;
  - W_ARGTYPE: a variable type vanilla never passes there;
  - W_CONFIGTYPE: a config id bound to another type in vanilla (the game logs `[impact] Config Variable '%k' type
    did not match` and keeps the default);
  - W_TYPESIZE, W_ALIGN; W_SITE: a vanilla site id reused.
- **All 571 vanilla programs pass** with no error and no warning. **[data]**

## 7. Lab: a tools/impact program in the running game [rt]
Lab `impact` (port 26130, a copied world, no player). Set-up:
- `tools/impact/lab/run.sh` cooks `tools/impact/lab/ImpactLab/impact/cook.json`: a clone of the Fireball explosion
  program `80a2448f` (same GUID, kiln.assets boot override) with a `spawnEntity(Enemy_Vukah_Melee)` inserted after
  its opening `getTransform`.
- The ImpactLab mod spawns `Explosion_Player_Spell_Fireball_1` far from everything.
- kiln.abilities was taken out of the lab for this run, so only the game's own program loading is involved.

```
assets: ready; {"resources":1,"resourcesNew":0,"resourcesOverride":1,"blobs":0,"icons":0}
impact-lab: ImpactRegistry count(+0xbc) = 571
impact-lab: JOIN 2543 Enemy_Vukah_Melee parent 0 byKiln False at (3859.8, 833.5, 1493.8)
impact-lab: program 0x7a120ee8 resolved 5 time(s) since the hook
impact-lab: PASS override program ran: its spawnEntity made Enemy_Vukah_Melee 2543 (parent 0) for explosion 2542
```

So the game's ImpactRegistry loads program resources through the resource service that kiln.assets answers. A
tools/impact blob (315 words instead of 291, two variables added, jumps relocated) is accepted, and its inserted code
runs on spawn without a player. The game fills the registry after kiln.abilities' first look at it: at that point
it held 0 programs (capacity 2048).

**With kiln.abilities** (the same lab, abilities-runtime's build of 12:05, ImpactSample deployed):
- kiln.abilities registered the four cooked programs from kiln.assets. Its verifier's max depths match `impact.py
  lint`: heal 6, chain 25, signal_projectile 13, signal_explosion 25.
- `signal_explosion` waited until the mod registered its managed native.
- Both proofs then passed without a player:

```
kiln.abilities: program 7e24a487-bff5-4bae-aefc-b273059dc90f (0x1ac5ce80) registered from kiln.assets …: 377 code words, max stack depth 25
kiln.abilities: program kiln.assets 3db38d34-9ac2-4ebb-8a49-a3774d25b7cd waits for natives
kiln.abilities: program 3db38d34-… (0xd64e7592) registered from kiln.assets …: 305 code words, max stack depth 25
impactsample: signal 0x5a11 from impact 16777217 (program 0xd64e7592, self 2542, root 0) site 0x28b17c51: entity 2542
impactsample: Ping from impact 16777217 (program 0xd64e7592, self 2542, root 0) site 0x27b17abe: entity 2542
impactsample: PASS signal_explosion: signal from self 2542 (spawned 2542), Ping called 1x
impactsample: signal 0xc4a1 from impact 50331649 (program 0x1ac5ce80, self 2547, root 0) site 0x1cde4c33: entity 2543
impactsample: PASS chain_explosion: chained to 2543 (Vukah 2543)
```

So a cloned program with inserted code works end to end, and so do a query loop at a new site (queryCallIds from
`queries auto`), new variables with initial values (8 m range, the faction mask, the template GUID), the built-in
`kiln.Signal` and a managed Kiln native.

## 8. Open questions
1. Who reads `stackSize` (if anyone), and is the client's instance stack also 32 slots? (The client interpreter is
   the same code [hyp]; createImpactInstance on the client was not decompiled.)
2. When exactly `codeShutdown` runs and with what state (EMF plumbing §5.2, not re-checked).
3. The variable `name` hash rule (only COPY uses it; tools/impact uses FNV-1a(dbgName) for new variables).
4. The argument meaning of the rarely used natives tagged `hyp` in natives-notes.json (clamp, fromTransform,
   getHitTarget, …).
5. What a query native does at a site missing from queryCallIds (refused by the verifier, not tested).
6. Whether a program registered at run time can call a Kiln native registered later in the same step (abilities-runtime:
   an unregistered hash errors only that instance, §4).


## 9. ECALL and synchronous natives (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; source: dev.28 dossier, chapter 06c and systems VERIFY (ECALL/context confirmed;
RegisterNative confirmed with the bool-return correction). These addresses supersede the older-build RVAs above
for the named entry points only. [static]

| Role | Server | Client | Contract |
|---|---|---|---|
| `abilities/Interpreter` | `0x990910` | `0x1239a30` | `interp(Instance*, Slice<u32>*, ImpactProgram*, EcsIterCtx*)` |
| RunImpact | `0x99c1d0` | `0x1245000` | calls interpreter at S `0x99c235` / C `0x1245065` |
| ECALL `call r10` | `0x991271` | `0x123a391` | `fn(Result*, ArgSlice*, NativeCtx*)`; setup site S `0x99125e` / C `0x123a37e` |
| `abilities/RegisterNative` | `0x996b70` | `0x123fc90` | `bool(Registry*, u32 hash, NativeFn, const Slice<char>* name, u8 argc)` |

At ECALL: Result is `rbp-0x68`, ArgSlice `rbp-0x50` (`{u32* data=rbp+0x38,u64 count}`), NativeCtx `rbp-0x30`.
Result is error byte `+0`, zero u32 `+4`, value byte `+8`. Args pop top-first. Error 0 pushes value byte; 1..3
suspends without consuming this ECALL, other errors stop the instance; a null native pushes 1. Variable/out
parameters, not that pushed status byte, carry typed results. [static]

NativeCtx: layout/count `+0/+8`, instance data/size `+0x10/+0x18`, **impact_simulation's iterator** `+0x30`, RNG
`+0x38`, instance id `+0x40`, GUID `+0x44`, ProgramId `+0x54`, call-site id `+0x58`. The iterator is passed by
impact_simulation's RunImpact call at S `0x197ee8`; its registry is at iterator `+0x110`. It is a borrowed callback
context, not a world pointer or a context valid in a later tick. [static]

Registry values have fn `+0`, name `+8`, argc `+0x48`. Capacity 256, 107 vanilla keys (108 registrations).
All 108 calls are in **server-world setup** S `0x233ee0` / C `0x49be10`, never client-world setup C `0x3a60b0`.
Hosted execution observed RegisterNative's fifth stack argument; joined session 4 registered no natives. Narrow argc
must be read as u8: upper stack-slot bytes are garbage. [static] [lab] [seen]

Natives execute on the `impact_simulation` slice's thread, not a universally safe mod tick. Flags `0x1200`
(CHUNKED | `0x1000`) and the world's pin byte kept this program on the world thread on the dedicated and hosted
worlds observed; it did not run in client or prediction worlds. See [world.md](world.md), Scheduler and threads.
This is not proof of arbitrary-thread/re-entrant access to world APIs. [static] [lab] [seen]

[lab] Existing managed `Natives.Register` suspended and answered **one step later** (14 cycles). A synchronous
vanilla-native wrap checking sint32 arguments, writing an out-variable and returning 1 answered in the **same step**
(14 cycles). A filtered getTransform wrap saw vanilla Fireball explosion ProgramId `0x7a120ee8`, site `0xfcce4e0a`,
`FromKiln=false`, without cloning the program. These prove the native-call shape, not a shipped new synchronous API.
A site is not a unique occurrence: main/shutdown apply/revert can share it. Identify an attachment by
**(ProgramId, site, native hash)**, optionally stream, not site alone. [data] [static]

Scratch `@abilities/ECall.site` captures displacements `ctx=0xd0`, `args=0xb0`, `result=0x98` from its anchor;
`abilities/Interpreter` and this site resolve once on S/C and old server `d7baa768…` (svn 1273043). They are fallback
inspection/hook anchors: the tested wrap works through the registry, with no ECALL instruction patch. Typed-hook
Win64 lane/hidden-return evidence is in [hooks.md](hooks.md); full API proposals remain in dossier chapters 06b/06c.
