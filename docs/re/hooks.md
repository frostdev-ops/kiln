# Native call ABI and hook evidence (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; old server O `d7baa768…` (svn 1273043) where stated.
Source: dev.28 dossier chapter 06b, systems REPORT/VERIFY. This records engine call contracts and probe results,
not the proposed typed-hook API. Related mechanisms are in [world.md](world.md) and [impact.md](impact.md).

## Windows x64 lanes [static]

First four argument **positions** use RCX/RDX/R8/R9 or XMM0..3 by type: a float in argument position 2 uses **XMM1**,
not RDX. Fifth and later arguments start at entry RSP+0x28, above the 32-byte home space. Integer/pointer results
use RAX, float results XMM0. An indirect struct return buffer occupies the first argument position, shifting all
following positions; the indirect-return functions below return that buffer pointer in RAX. Narrow u8/u16 arguments must
be masked to their width, especially stack slots whose upper bytes are not initialized.

## Current helper contracts [static], runtime evidence as marked

| Symbol | S / C RVA | Observed Win64 signature |
|---|---|---|
| `items/GetItemVolume` | `0x1bb380` / `0x41af40` | `OptS32*(OptS32* out, const ItemInfo*)`; O `0x1b5b10` |
| `items/AttrFind` | `0x991810` / `0x123a930` | `AttrRef*(AttrRef* out, u32 attribute, void* registry /*map +0x68*/)` [lab: kiln.items] |
| `items/AttrChanged` | `0x52700` / `0x2358c0` | `void(void* component, void* registry)` [lab] |
| `time/SetTimeScale` | `0x95b640` / `0xeb39d0` | `void(Daytime*, float scale /*XMM1*/, s64 ecsNow /*R8*/)` [lab] |
| `time/GetGameTime` | `0x937a20` / `0xe908e0` | `s64(const Daytime*, s64 ecsNow)` [lab: kiln.time] |
| `abilities/RegisterNative` | `0x996b70` / `0x123fc90` | **`bool`** `(Registry*,u32 hash,NativeFn,const Slice<char>* name,u8 argc /*stack arg 5*/)` [lab] [seen] |
| `world/ReadOther` | `0x64e9e0` / `0x9b2b50` | `Slice*(Slice* out,Storage*,const u16* accessor,u32 entity)` [lab] |
| core GetComponent | `0x64ed50` / `0x9b2ec0` | `void(Slice* out,World*,u32 componentHash,u32 entity)` [lab] |
| `rules/QuatRotate` | `0x3ba80` / `0x21e760` | `float3*(float3* out,const Quaternion*,const float3*)` [lab: rules glide] |
| `weather/SampleAt` | `0xa21940` / `0xaaa4d0` | `WeatherSample*(out,const WeatherSystem*,const WorldTransform*,s64 now)`; O `0xa16cc0` |

OptS32 is 8 B / align 4: s32 value `+0`, has byte `+4`. Null item yields `{18,1}`; missing Volume sets only has=0.
AttrRef is 16 B / align 8: error byte `+0` (0 or `0x1f`), zero u32 `+4`, pointer `+8`.
WeatherSample is 16 B / align 4, **four floats** at `+0/+4/+8/+0xc`, meanings not decoded; its fourth argument is
a **64-bit time value**, not a Time pointer. Systems VERIFY corrected the weather prototype and RegisterNative's
success-byte return. Weather call-site capture and preconditions are in [world.md](world.md), Weather sampling ABI.
These memory results/pointers are borrowed from the caller/world, not safe to retain across callbacks or teardown.

ReadOther is a jump-through: RDX/R8/R9 are forwarded, not all read before branching. A static ABI fingerprint must
include pass-through lanes; a short entry inspection alone does not establish a one-argument function. RegisterNative
is a world-setup seam, ReadOther needs a real declared program accessor/storage, and AttrChanged belongs inside the
attribute program path. Resolving these addresses does not authorize arbitrary-thread invocation. Daytime calls use
the live server singleton/time at a safe tick boundary; component slices are step-local. [static]

`rules/TerrainDamage` S `0x22c970` / C `0x495410`: `TerrainHit*(out,X* rdx,const HardnessSetup* r8,u8 r9,u8 cls
/*stack arg 5,0..3*/)` [static]; setup floats `+0x14/+0x18/+0x1c`. Meanings of RDX/R9 remain **[inferred]**; this
is not a complete safe-call contract. No varargs or universal physics/terrain call wrapper was established.

## Probe evidence, not a shipped typed ABI [lab] [seen]

A native frame thunk plus call_with_frame passed the actual SetTimeScale float in XMM1 through the existing hook
chain and read back the unchanged scale. RegisterNative's fifth stack argument matched argc (108 vanilla
registrations plus Kiln's); hosted client execution observed the same. A synthetic six-argument function with mixed
integer/float/double registers, two stack arguments and float result returned the expected original and modified
values. Per-call overhead was **not measured**.

At the researched revision, the legacy structured integer-slot hook saves/forwards integer lanes but not XMM
arguments/returns; native raw_detour with the exact signature is the existing float path. These probe results do
not retroactively make an integer-only detour float-safe. Full frame/thunk code, fingerprint policy and proposed
API/generator designs stay in chapter 06b. No new product hook API or production sigdb entry is implied here.
