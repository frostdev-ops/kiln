# EMF 0.3.3 `emf_core.dll`: what we learned

Source: EMF (Enshrouded Modding Framework) 0.3.3, MIT, "EMF contributors". Binary sha256
`f3abc4a1d41a120a7b24e526306e4c725ea3e5414e02c2f3f47acb2e981041e7`. The core's source is not public; this is
from static analysis (Ghidra 12.1.2) plus EMF's own docs (`docs/ENGINE_MAP.md`, `docs/engine_parts/*.md`).

## Signature table
- 51 entries at VA `0x18029DB50`, stride `0x38`:
  `u64 index; const char* name; const char* pattern; u32 rva_client_1269579; u32 rva_client_1273043;
   u32 rva_server_1273043; u32 required; u32 extra`.
- Preceded by the known-build list: sha256 strings with build tags (client 1273043 `d64966e2…`,
  server 1273043 `d7baa768…`).
- Extracted to `sigdb/evidence/emf-0.3.3-signatures.json` by `tools/sigport/emf_table.py`.
- Only `SimStep`, `GetComponent`, `RegisterSingleton` are required; when one is missing EMF goes idle
  ("a required signature is missing: EMF stays idle, no mods loaded"). Every other feature checks its own
  signatures ("give_item refused: the SpawnWrapper signature is not usable on this game build").

## Porting to server build 26404334 (svn 1285601, exe sha `c5c9fe50…`)
`python3 tools/sigport/emf_table.py <server 1273043 exe> <current exe>`:
- 38 of 51 resolve uniquely, 5 of them after rebasing world-struct displacements by `+0x1C00`
  (the program table grew: stride `0x1F20` → `0x1F28` × `0x380` slots). Examples: `world.storage` `0xE15DC0` →
  `0xE179C0`, `world.tick` `0xE1629C` → `0xE17E9C`.
- 9 are client-only (no server RVA): ClientWorldSetup, QueueChatMessage, UiFrame, PregameState,
  PostSelectCharacter, PostSelectWorld, PostSessionArea, PostStart, CharacterSelected.
- The last 4 server signatures were ported by hand (`sigdb/evidence/server-26404334-hand-ports.json`), so every
  server signature EMF uses now resolves uniquely:
  - SpawnWrapper `0x620d00`: EMF's pattern cut the `world.storage` displacement after two bytes (`C0 5D`), so the
    automatic rebase missed it; with the full rebased displacement (`C0 79 E1 00`) it matches once.
  - IterNext `0x65e610` and CurrentEntityId `0x65b870`: the program context's slice fields moved 8 bytes, entity
    list `+0x828` → `+0x830` and entity count `+0x830` → `+0x838` (same +8 as the program-table stride).
  - AllocPendingHit `0x98c040`: same logic (pool `+0x110`, fallback pool `+0x138`, flag byte `+0x144`); the frame
    grew `0x20` → `0x40` for a new "both pools empty" log call.

## Confirmed live (lab, Kiln spike `native/spikes/tickprobe`)
- SimStep at RVA `0x6c8f10` hooks cleanly with safetyhook; runs on one server thread for world kind 0, about
  24 calls/s, ~1.1 ms per step at idle under Wine.
- **Tick model (measured, lab2, plain Wine):** on the dedicated server SimStep for world kind 0 runs every 40.9 ms
  (p50; 24.5 Hz). The u32 at `world + 0xE17E9C` (= `storage + 0x4DC`, EMF's "tick") never changes inside SimStep;
  between two calls it advances by 2 or 3 (≈ 55 % / 45 %, mean 2.45). So it is a 60 Hz game clock (EMF:
  `ecsNow = tickPeriod(storage+0x4C0) × tick`) advanced outside SimStep, not a step counter. EMF's "60 Hz, 16.6 ms
  step" was measured in a client-hosted (LOCAL) world, where the client frame drives the server world.
  - Kiln consequence: `OnTick` = one SimStep (24.5 Hz on dedicated servers, up to 60 Hz on listen servers). The tick
    event carries `Tick.Number` (the 60 Hz clock) and `Tick.Delta` (clock advance since the previous step). Mods must
    use the delta, never count steps as time.
- .NET 10.0.14 (private win-x64 runtime under `kiln/dotnet`) boots via hostfxr in ~130 ms; `[UnmanagedCallersOnly]`
  calls from the server thread cost 0.008–0.04 ms. GC under extreme allocation stress: first gen0 pause 8.4 ms,
  then 0.7–5 ms per 250 ticks. Tune the gen0 budget.
