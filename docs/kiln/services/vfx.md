# kiln.vfx / Kiln.Vfx: play any VFX from code

Play the game's VFX, or a mod's, at a position or attached to an entity and slot. A guard means no VFX GUID can abort the client. Forge:
`Level.addParticle`, `ParticleEngine`. Research: [docs/re/vfx.md](../../re/vfx.md).

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/client.d/vfx.json`, features `vfx.guard`, `vfx.play` |
| C table | `native/core/abi/services/kiln_vfx.h` (`kiln.vfx` v1) |
| Native | `native/services/vfx/` → `kiln/services/kiln-vfx.dll`. Client only |
| Managed | `managed/services/Kiln.Vfx/` → `kiln/host/Kiln.Vfx.dll` (namespace `Kiln`: `Vfx`, `VfxId`, `VfxHandle`, `VfxParams`, `Slot`, `Offset`) |
| Network | Kiln channel `kiln:vfx` v1, `required: false` |
| Test | `mods/tests/vfx-test`, `native/services/vfx/tests/run.sh` |

## C#
```xml
<ProjectReference Include="…/managed/services/Kiln.Vfx/Kiln.Vfx.csproj" Private="false" ExcludeAssets="runtime" />
```
```csharp
using Kiln;

static readonly VfxId Boom = VfxId.Game("Explosion_Player_Spell_Fireball_1");   // a template that carries it, or a sequence that plays it
static readonly VfxId Mine = VfxId.Named("my-mod", "frost_burst");              // UUIDv5(<mod>:vfx:<name>). What Blocks.Define returns for a VfxBlock works as well

var h = Vfx.PlayAt(Boom, position);                                             // a one-shot
var aura = Vfx.PlayOn(Mine, entity, Slot.HandR, new Offset(new Vector3(0, 0.1f, 0)));   // moves with the entity, ends with it
Vfx.Set(aura, "radius", 4f);                                                    // float, int, Vector3, Color
Vfx.Stop(aura);                                                                 // Stop(aura, immediate: true) destroys it at the next update
Vfx.PlayAt(Boom, position, parameters: new VfxParams().Set("color", new Color(40, 120, 255)));
Vfx.PlayAt(Boom, position, lifetime: Vfx.UntilStopped);                         // a loop: runs until Stop
Vfx.Preload(Boom);                                                              // its first real play won't start late
```
- **Names.** VFX have none of their own. `VfxId.Game` takes a template that carries one (`VfxComponentResource.vfx`), a sequence that plays one (its first
  `VfxNotifierEvent`), or a GUID's text.
- **Client:** any thread. The call returns a handle at once, and the game is called inside its next FxController update. With no world there is no controller:
  the request waits 10 s, then fails.
- **Server** (a dedicated server, or a hosting client's server-world tick thread): `PlayAt`, `PlayOn`, `PlayFor`, `Stop` and `Set` relay to Kiln clients
  within `radius` metres (0 = 120, negative = all), and a host's own client plays them too, once: it drops the copy the relay sends its own player. The
  handle is remote: it can be stopped and set, not observed.
  Vanilla clients see nothing, and a joiner doesn't see what started before. For what every player must see, start the effect from data: an entity, a
  sequence or a buff.
- **Entity ids.** On a joined client, ids aren't proven to match the server's. `PlayOn` sends id and position, and the client attaches only if its entity of that id
  stands within 10 m of the position, otherwise plays at the position.

## Parameters
Parameters given to a play go into the block the VFX starts with, over its defaults, as the game's own templates and sequences pass them, so a burst
that spawns at once has them. The play line then ends in `N parameter(s) in its start block`. A name or type the VFX doesn't describe goes to the
game's setter once there is an instance, and the game ignores it. `Vfx.Set` on a running VFX also uses the setter: only what spawns or is read
after it changes.

A Kiln VFX (a `VfxBlock`) describes `color`, `size`, `speed`, `brightness` and `count`, all factors on what its block says
([vfx-cook.md](vfx-cook.md#per-play-parameters)). Colour, size and brightness are read at every draw, so `Vfx.Set` changes them on what is alive;
count and speed apply to what spawns afterwards.

## How long an effect runs
The game reports no "finished", so Kiln stops a one-shot itself. A [VFX block](vfx-cook.md) stops after its delay, duration and longest particle life. Any other
VFX, and a block that runs until stopped, stops after the length its resource gives: the larger of `killTimeInSeconds` and its `duration` + `lifetime`
parameters, 10 s if it says nothing, 1 to 60 s. Pass `lifetime` to say otherwise, or `Vfx.UntilStopped` for a loop.

After a stop an instance stays for its kill time at most (10 s for almost every game VFX). Measured, it goes well before that, and a stop doesn't remove what's
alive at once: use `immediate` for that. `PlayOn` runs until `Stop` or until the entity is gone. An effect that gets no instance within 5 s (a slot this body
doesn't have, a full pool) ends with `VfxEndReason.NoInstance`.

## The guard
`VfxResolve` aborts the process on a GUID the VFX system didn't register. Kiln checks three times:

| Where | For | On an unknown GUID |
|---|---|---|
| Kiln's queue | `Vfx.*` | `vfx: #N unknown VFX <guid> ignored`, the game isn't called |
| `FxStart` | everything through the FxController: templates, sequences, buffs | the GUID is emptied, and the game's own path ends the entry |
| `VfxCreateInstance` | the callers without a controller | handle 0, the path an empty instance pool takes |

The two hooks log `vfx: unknown VFX <guid> ignored (guard at …; the game would have aborted)` once per GUID and place, and count every refusal
in `Vfx.Status().Refused`. Emptying at `FxStart` matters: a start that gets handle 0 retries every frame for ever, holding a pool slot that sounds share.

At the first update the guard checks that the map reads as the signatures promise (the occupancy bits add up to the count). If it doesn't, or a read faults, the
guard turns off and the game runs as vanilla, and kiln.vfx plays nothing, since it can't tell a safe GUID from an unsafe one.

## Fail soft
| Case | What happens |
|---|---|
| Guard signatures missing on this build | `vfx: the guard's signatures did not resolve on this game build (sigdb client.d/vfx.json): kiln.vfx is off, mods play nothing`. Nothing hooked. `Vfx.Available` false, calls return `VfxHandle.None`, one warning |
| Play signatures missing | The guard runs: `vfx: feature vfx.play is off on this game build …: the guard runs, mods play nothing` |
| A game call faults | `vfx: … faulted: kiln.vfx plays nothing more this session (the guard stays on)` |
| `kiln/config/vfx.json` `{"enabled": false}` | `vfx: off by kiln/config/vfx.json: nothing hooked, mods play nothing` |
| Dedicated server | No `kiln.vfx`; calls relay |

## Threads
`FxUpdate` runs on the client world's thread (measured). The queue runs inside it, with the access rights the game's own FX calls have. Nothing in the VFX path takes a
lock, so a call from anywhere else could corrupt its free lists. Mods never call the game directly.

## Testing
No client needed:
```sh
native/services/vfx/tests/run.sh [real-guids.txt [real-resources.txt]]   # the queue, the guard's logic, on a host compiler
python3 tools/sigport/sigdb_check.py sigdb/symbols/client.json <enshrouded.exe>
# A lab server: relay and the service being absent
mods/tests/vfx-test/fixtures/relay_client.py --port <labPort> --lab <lab dir>   # compares every kiln:vfx message byte by byte
# The same lab with kiln/config/vfx.json {"sim": true}: the whole path from Kiln.Vfx to a simulated game
# {"testFailSoft": true}: the real game layer on an exe where no signature resolves
```

### What else the game draws
`kiln/config/vfx.json` `{"trace": true}` (or a number of seconds, default 3) logs every VFX instance the game creates within that time of a play
from Kiln, at most 400 lines:
`vfx: trace: the game created <guid>, the VFX's own defaults, 12 ms after Kiln's last play, from +1f3c9f5 +2a1c9f …`. `Kiln` or `the game` says whose play
it was, a parameter block shows its first 16 bytes, and the addresses are the game's code that asked for it. For telling an effect of a mod from
something the game draws next to it. Remove the setting afterwards.

### Manual test
`mods/tests/vfx-test` on a client; lines `vfx-test: PASS|FAIL` in `kiln.log`. Each case is on a key and on `/vfxtest <case>`:

| Key | Case | You should see | `kiln.log` |
|---|---|---|---|
| Ctrl+1 | A game VFX 5 m ahead | The explosion | `vfx: #N play … at … -> handle 0x…, instance 0x…`, `PASS case 1: it ended by itself` |
| Ctrl+2 | A GUID nobody registered, through Kiln's check and both guards | Nothing; the game keeps running | `PASS case 2: …` three times, `INFO case 2 done` |
| Ctrl+3 | A flame on the right hand for 5 s | It walks with you, then stops | `… on entity 1 slot 8 …`, `PASS case 3: attached` |
| Ctrl+Shift+3 | Parameters, then a loop stopped immediately | A blue explosion; 3 s later a flame for 3 s, gone at once | `PASS case 3b: …` |
| Ctrl+4 | An effect built from `blocks/vfx.json` | Red squares fly up and fade | `PASS case 4: red_burst plays` |
| Ctrl+Shift+4 | The same effect on a game module | Tells a data fault from a shader fault when Ctrl+4 shows nothing | `PASS case 4b: …` |

## The contract (dev.27)

Kiln.Vfx follows [the API contract](../modding/contract.md) as far as it applies: its calls never throw for a missing capability (they return
`VfxHandle.None`), so no member carries `[RequiresCapability]`.

- **Capabilities.** In the game client: `kiln.vfx` and `vfx.play` (and `kiln.screen` for screen effects). On the server
  side: `vfx.relay` (managed, session, refreshed every 25 steps) says what `Vfx.Available` says there: available while
  a Kiln client with the `kiln:vfx` channel is connected (or this host's own client can play), pending while nobody
  listens, unavailable without the channel (`needs kiln.net`), `wrong-side` in a client that only joined.
  `screen.relay` is the same for `ScreenEffects.Available`, where an open `kiln:screen` channel is enough.
- **Threads.** `State`, `IsPlaying`, `Info`, `Status`, `StatusJson` and `ScreenEffects.IsPlaying` / `Status` are
  `[AnyThread]`. The play, stop and set calls carry no thread attribute: any thread in the game client, the tick
  thread on the server side (the relay).
