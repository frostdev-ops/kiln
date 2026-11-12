# kiln.abilities

Scripted abilities, per-cast tweaks of the game's own spells and attacks, Kiln natives for Impact programs, runtime
registration of new Impact programs, and playing the game's action sequences on any actor. Evidence:
[docs/re/abilities.md](../../re/abilities.md). Tools side of programs:
[docs/kiln/modding/impact.md](../modding/impact.md) (`tools/impact`).

| Layer | Path |
|---|---|
| C table | `native/core/abi/services/kiln_abilities.h` (`kiln.abilities` v1) |
| Native | `native/services/abilities/` → `kiln/services/kiln-abilities.dll` |
| Symbols | `sigdb/symbols/server.d/abilities.json` (features `abilities.natives`, `.programs`, `.instances`, `.attributes`, `.sequences`); resolves unchanged on the client (no `client.d` overlay needed) |
| Managed | `managed/services/Kiln.Abilities/` → `kiln/host/Kiln.Abilities.dll` (static class `Kiln.Abilities` + events) |
| Test | `mods/tests/abilities-test/` (83 PASS and one known FAIL, the bow shot, which `PlaySequence` refuses because its event consumes the used item; player-free; on a client, or with `kiln/data/abilities-test/session-only`, it only registers the `/abt` session commands) |
| Sample | `mods/samples/FireNova/` (side both: a keybind ability + a per-cast tweak of the game's Fireball) |

## What the game does (one paragraph)
Every spell, weapon attack, perk, buff and enemy attack is data plus one VM: an **action sequence** (timed events:
animation state, VFX/SFX, SpawnImpact/SpawnEntity, PayUsageCost, ability locks) spawns entities from **templates**
whose `ecs::Impact` component runs an **Impact program** (u32 bytecode, 30 opcodes) that calls **natives**
(applyDamage, applyBuff, spawnEntity, ... 107 of them). Programs read **config values** (Damage, AttackMod, NoiseRange,
SpawnTemplate, BuffType, ...) that the game copies into a new instance from the template, the sequence event, the
used item, the buff or the skill node. All of it runs on the server; clients only see the results (entities by
template GUID, hits, buffs, replicated sequence ids). So everything below works with **vanilla clients**, except the
key binding of a scripted ability, which needs Kiln and the mod on the caster's client (chat commands and server-side
triggers do not).

## C# quick start

Reference it like every service: `<ProjectReference Include=".../managed/services/Kiln.Abilities/Kiln.Abilities.csproj"
Private="false" ExcludeAssets="runtime" />`. Everything runs on the tick thread unless a section says "job thread".

```csharp
using Kiln;

// 1. A scripted ability (a side "both" mod registers it on both sides; the server casts, a Kiln client binds G)
Abilities.Register(new AbilityDefinition
{
    Id = "firenova:fire-nova", DisplayName = "Fire Nova", DefaultKey = "G", ChatCommand = "nova",
    Cost = AbilityCost.OfMana(40), Cooldown = TimeSpan.FromSeconds(5), CastTime = TimeSpan.FromSeconds(0.5),
    Targeting = AbilityTargeting.Self,            // Self | Forward (Range m ahead) | NearestEnemy (within Range)
    CastSequence = null,                          // optional: an ActorSequenceResource GUID the caster plays
    OnCast = async cast =>                        // server, tick thread
    {
        // root = caster; a Kiln impact has no used item, so give it its own damage (else it hits for 0)
        await cast.Impact("Explosion_Player_Spell_Fireball_1", cast.CasterPosition,
            new ImpactConfigs { ["Damage1"] = DamageSetup.FireOnly(10f) });
        await cast.At(TimeSpan.FromSeconds(0.3));                                          // timed effects
        await cast.Impact("Explosion_Player_Spell_Fireball_1", cast.Position,
            new ImpactConfigs { ["AttackMod"] = 1.5f, ["NoiseRange"] = 40f });              // chosen configs
    },
});
CastResult r = Abilities.Cast(npcOrPlayer, "firenova:fire-nova");   // server-side trigger (any actor with the cost)

// 2. A per-cast tweak of the game's own Fireball (before the explosion's first run)
Abilities.HoldPrograms(0x7a120ee8);               // only this program waits for the handlers (see "holds")
[Subscribe] void OnImpact(ImpactStartingEvent e)
{
    if (e.RootIsPlayer && e.TemplateName?.StartsWith("Explosion_Player_Spell_Fireball") == true && IsNight())
        e.Scale("AttackMod", 2f);                 // or e.Set("NoiseRange", 40f), e.Cancel()
}

// 3. Run existing impacts and programs with chosen configs, bound to a caster
EntityId boom = await Abilities.RunImpact("Explosion_Player_Spell_Fireball_1", pos,
    new ImpactOptions { Root = caster, Configs = new() { ["Damage1"] = DamageSetup.IceOnly(1f) } });
EntityId p = await Abilities.RunProgram(myProgramGuid, pos, new ImpactOptions { Root = caster });

// 4. Play an existing action sequence (animation + its own timed impacts) on a player, NPC or creature
Abilities.PlaySequence(vukah, new Guid("a837f190-d163-4ef5-a48e-3ea7caac7296"));   // the Vukah's melee swing
Abilities.PlaySequence(player, new Guid("13fd798d-033a-494b-95e8-76fe817202b5"), SequenceOptions.Queue);  // the Greet emote
Abilities.PlaySequence(player, new Guid("deff43ba-530a-4bee-be84-9b3c781e9556"));  // a staff cast: input states held

// 5. A Kiln native for Impact programs (ECALL "mymod.Chill" from a program; see the contract below)
Abilities.Natives.Register("Chill", argc: 2, call =>
{
    var target = call.GetEntity(1); var seconds = call.GetFloat(0);
    Combat.Damage(target, (int)(seconds * 3), call.Impact.Root);
    return 1;                                    // the value the ECALL pushes (the program gets it one step later)
});

// 6. Observe (one step late) and wrap (synchronous, job thread) the game's natives
[Subscribe] void OnHit(ImpactDamageEvent e) => Log.Info($"{e.Attacker} -> {e.Target} AttackMod {e.AttackMod} {e.Damage}");
Abilities.OnDamage((ref DamageCall c) => { if (c.Attacker.IsPlayer && c.Target == boss) c.AttackMod *= 0.5f; });

// 7. Buffs through the game's applyBuff (a BuffType GUID), resources and programs
await Abilities.ApplyBuff(target, new Guid("be0e7626-e95f-4a14-844a-2af52501698e"), TimeSpan.FromSeconds(6), caster);
int? mana = Abilities.Attributes.Get(player, AbilityResource.Mana);        // Health, Mana, Stamina, SpecialAbility
Abilities.Attributes.Add(player, AbilityResource.Stamina, -10);
uint id = Abilities.Programs.Register(File.ReadAllBytes("my.kimp.bin"));   // usually: kiln.assets resources (below)
```

## Scripted abilities
`Abilities.Register(AbilityDefinition)` returns an `AbilityHandle` (Dispose to remove; also on mod unload).

| Field | Meaning |
|---|---|
| `Id` | `"<modid>:<name>"`, the same on both sides |
| `DefaultKey` | client key ("G", "Shift+F"); the player remaps it in the Kiln menu's Keys tab. Null = no key |
| `ChatCommand` | a chat command that casts it (`/nova`); `/cast <id>` always works. Vanilla players can use both |
| `Cost` | `AbilityCost(Mana, Stamina, Special)` attribute points, checked then paid when the cast starts |
| `Cooldown` | Kiln's own, per caster (the game has no player cooldown system) |
| `CastTime` | cast lock: no other Kiln ability for this long (the game's own attacks are not locked) |
| `CastSequence` | optional action sequence the caster plays at cast start (`PlaySequence`) |
| `Targeting`, `Range` | `Self`, `Forward` (a point `Range` m ahead of the caster's body yaw), `NearestEnemy` (refused without one) |
| `PlayersOnly` | keys and commands only for players (default); `Abilities.Cast` from server code works for any actor |
| `CanCast` | extra check: return a refusal reason or null |
| `OnCast` | `Func<AbilityCast, Task>` on the server's tick thread; `cast.At(t)` waits until t after the start |

Cast flow (server): unknown ability → cast lock ("busy casting") → cooldown ("cooldown 3.2 s") → targeting →
`CanCast` → `AbilityCastEvent` (cancelable; `Cost` writable; `Reason`) → cost check ("not enough mana (12/40)") → pay
→ cooldown + lock start → `CastSequence` → `AbilityCastedEvent` → `OnCast` (exceptions go to the mod's log).

Triggers:
- **Key** (Kiln client with the mod): `KeyBindings` → channel `kiln:abilities` (`CastRequest`, not required for mod
  sync) → the server validates with the sender's player entity → `CastReply` (refusal toast, a HUD cooldown line
  bottom right).
- **Chat**: `/cast <id>` and the ability's `ChatCommand` (Kiln.Chat), players only.
- **Server**: `Abilities.Cast(caster, id)` from any handler (a kill, an interact, a timer, an NPC's turn).

`AbilityCast`: `Caster`, `Target`, `Position`, `CasterPosition`, `CasterYaw`, `Origin`, `At(TimeSpan)`,
`Impact(template, at, configs)` (`RunImpact` with `Root = Caster`), `Damage(target, amount)` (kiln.combat),
`PlaySequence(guid)`. `Abilities.CooldownLeft/ResetCooldown`.

## Per-cast tweaks of the game's own abilities
**`ImpactStartingEvent`** (cancelable): an instance exists with the game's config values and has **not run yet**.
Change its variables (`Set(key, value)`, `SetFloat`, `Scale`, `Instance.Write`) or `Cancel()` (its entity is
destroyed before the first run). Keys: a variable alias or debug name of the program (`"NoiseRange"`, `"Damage1"`,
`"SpawnTemplate_Pre"`), a config id (`"0xa19409b2"`) or an alias of `ImpactConfigIds` (`AttackMod`, `Damage`,
`BuffType`, `Duration`, ...). Values: float, int, bool, `TimeSpan`, `Guid` (template/buff references), `DamageSetup`,
`byte[]`; converted to the variable's type.
- **Holds.** Handlers run on the tick thread, so while anyone subscribes Kiln holds each new instance for one step:
  its first `runImpact` is skipped until the handlers ran in the next Pre (about 41 ms on a dedicated server).
  `Abilities.HoldPrograms(ids)` narrows that to some programs (the union over all mods; no filter = every new
  instance is held). Lab: `held: the instance had not run when the handler saw it (flags 0x1)`, then `after release it
  ran with the handler's value: NoiseRange 33, flags 0x3`. Whether a held projectile or explosion still gets its
  first-step collision events is on the client checklist.
- **Observe only**: `ImpactStartedEvent` (the same moment, no hold), `ImpactEndedEvent`.
- **Natives**: `ImpactNativeEvent` and its typed forms (observe; posted in the next Pre with a copy of the arguments
  taken right after the call): `ImpactDamageEvent` (applyDamage: `Attacker` args[1], `Target` args[2], `Position`,
  `AttackMod` args[6], `Damage` args[14], `Blocked` args[0], `Result`), `ImpactBuffEvent` (`Buff`, `Target`,
  `Duration`), `ImpactSpawnEvent` (`Template`, `Position`, `Spawned`). `Abilities.Natives.Observe("getTransform")`
  adds any other vanilla native.
- **Rewrite or cancel a native call (job thread)**: `Abilities.Natives.Wrap(name, (ref NativeCall c) => …)` runs
  synchronously inside the sim step on the thread that runs the program: read and change argument variables
  (`GetFloat/SetFloat(i)`, `Bytes(i)`), `CallOriginal()`, `Return(v)` / `Cancel()`; the game's native runs after the
  last handler unless one decided. `Abilities.OnDamage((ref DamageCall c) => …)` is the typed applyDamage form
  (`AttackMod` changes this call only: Kiln restores the variable after the hit). Rules: touch only the call; no
  tick-thread API; thread-safe handlers; a throwing handler is logged (3 times) and skipped. On the dedicated server
  the lab saw these calls on the tick thread; a client host may run them on job threads.

## Running impacts, programs and sequences
- `RunImpact(template, position, ImpactOptions)`: spawns the template (DoNotSave, untracked unless `Track`) with
  `ImpactHierarchy {parent, root, -, permission} = Root` and the root's `Faction` (or `Faction`) as spawn components,
  then sets `Configs` as overrides that Kiln writes into the new instance after the game copied its own configs and
  before its first run. Lab: `the game copied the template's config: NoiseRange 25 (template 25, program default
  20)`, `overrides applied before the first run: NoiseRange 40, AttackMod 2.5, PostWaitTime 00:00:00.3000000`,
  `ImpactHierarchy root from the spawn component: 2542`.
- `RunProgram(programGuid, position, options)`: the game's generic impact template (`DefaultImpact`,
  `e97a20a4-4ea2-4767-89ae-c41ff45848c1`) with an `Impact` spawn component for that program. **Pass `Root`** for
  anything a player casts: without it the entity has no ImpactHierarchy and `getRoot` gives 0 (impact-asm saw
  `isEnemy(0, x)` still work with `Faction = 7` [hyp: it falls back to self]).
- `PlaySequence(actor, sequenceGuid, abortCurrent = true)` / `PlaySequence(actor, sequenceGuid, SequenceOptions)` on
  players, NPCs and creatures: the game's own helper (`abilities/TriggerSequence`, 0x369d0) that emotes, death, fall
  damage and 80 other call sites use: it fills `ActorInput.triggerContext` {sequenceRuntimeId {sequence, time, actor,
  callIndex}, trigger 24 ExternalAbortAndOverride (`AbortCurrent`, default) or 22 ExternalTrigger
  (`SequenceOptions.Queue`, the emotes' trigger)}, input type 0x38 as emotes; the actor's sequence system starts it in
  its next step and `NetworkActor` replicates it. Only sequence GUIDs the game has are accepted. Lab, no player: `the
  Vukah's Actor.sequenceRuntimeId is the swing now (0x485a4a65)` and `the swing's SpawnImpact events ran server-side: 2
  instance(s) of program 0c3a7176 (root 2542)`. A sequence also runs its own PayUsageCost and SpawnEntity events (a
  player's Fireball cast pays mana and fires the equipped ammo).
  - **Players**: the earlier "works on NPCs only" was the test sequence: a staff cast is input-gated
    (KeepAlive/Loop events wait for InRangeCombat / InRangeCombatShooting, which a player's held input sets) and ended
    at its first gate, 0.2 s in, before its animation. Emotes, melee attacks, consumables and wand attacks have no
    gates. `SequenceOptions.HoldInputs` (default true) holds those states in the running sequence's own state mask
    (replicated) until each gate has passed, each loop body plays once, then gives them back. Evidence:
    docs/re/abilities.md §9. Lab, a player-template entity (`1_Player`): `the Greet emote plays on the player
    character for 92 steps (3.83 s) ... (NetworkActor.triggerType 22)`, `a melee attack ... plays on the player for 41
    steps`, `Fireball cast: 5 steps without the hold, 46 with it (replicated states seen 0x40000e000000)`, `a bow shot:
    4 steps without the hold, 18 with it`.
  - C: `sequence_play(entity, id, trigger | 0x10000)` holds (the flag is a Kiln extension; older services answer
    `INVALID_ARGUMENT` and the managed API retries without it).
  - **In a vanilla client joined to a lab, the player's own character:** the Greet and Dance emotes (`queue`, trigger 22) animate on the player's own screen
   ; the greatsword whirl (trigger 24) swings the held tool with the game's hit markers on the
    chests it struck; the held staff cast runs 1.9 s on the server with
    `states 0x400008000000` (InRangeCombatShooting) replicated and the client shows the cast pose and the sequence's own
    ammunition HUD. A cast started this way spends nothing and fires nothing: `PayUsageCost` and the ammo's
    `SpawnEntityEvent` read the trigger context's used item / ammunition, which `PlaySequence` leaves empty (mana 255
    before and after). Filling `ActionSequenceTriggerContext.usedItem/usedAmmunition` (+0x30/+0x44 ItemInstanceInfo)
    from the actor's equipment is the next step for "cast the equipped spell"; FireNova-style abilities spawn their own
    impacts and are not affected.
- `ApplyBuff(target, buffGuid, duration?, source)`: a buff or debuff through the game's own applyBuff native, run by a
  small program Kiln builds and registers at first use (`6b696c6e-6275-4666-8000-000000000001`: applyBuff(Buff,
  Target, -, -, Duration), DSELF). A buff is named by its BuffType GUID; its id is FNV-1a of the GUID (the potion's
  heal-over-time `be0e7626-e95f-4a14-844a-2af52501698e` = `0x339519bd`). Buff GUIDs appear wherever data references a
  buff (`ApplyBuffEvent.buffTypeOverride`, BuffType configs); the collection itself lists only ids. Lab: `ApplyBuff ran
  the game's applyBuff ... result 1` and `the buff's own program (e34f34e4, 0x97faba7b) started on buff entity 2558
  (root 2543)`; that buff did not heal a Vukah (it probably heals players only: client checklist).
- `RemoveBuff(target, buffGuid)` and `RemoveBuff(target, buffId)` (dev.27): `Task<BuffRemoveResult>` through the game's
  own removeBuff native, run by a second Kiln program (`6b696c6e-6275-4666-8000-000000000002`:
  removeBuff(WasRemoved, Buff, Target), Done = 1, then up to 30 steps of waiting, DSELF). Kiln reads `Done` and
  `WasRemoved` from the live program instance at each step and destroys the helper entity once it has them, so the
  answer does not ride the native event queue (which drops events when it is full); a helper nobody read ends
  itself. The answer arrives about two steps after the call. `Ok` is the game's own `wasRemoved`: false, with a
  reason, when the entity did not have the buff, when the game refused the helper entity, when the helper went away
  before it was read, when the server world closed meanwhile (the task still completes: a hosted session that ends
  leaves nothing waiting), or, after 120 steps, when the program never answered. On success
  `BuffRemovedEvent(Target, Buff, Mod)` is posted first. The id overload passes 16 bytes with
  that id's FNV-1a (the game hashes buff references and reads nothing else), so a `GameBuffs.Ids` constant or a listed
  `EntityBuff.Id` works. The game answers at once, but clears the BuffContainer slot and destroys the buff entity one
  step after the answer: read `BuffsOf` a step later to see it gone. ApplyBuff of a buff the entity already has keeps
  one entry and one buff entity (it renews it; a 6 s buff renewed by a 60 s ApplyBuff showed 5.7 s left, so the
  renewal does not take the new duration), so there is never a second stack to remove: one removeBuff takes the buff
  off. Server, tick thread; a none or absent target throws `ArgumentException`, capability `abilities.buffs` off throws
  `KilnUnavailableException`. Lab (abilities-test): `RemoveBuff: the game's removeBuff says removed ... (2 steps)`,
  `at the answer the slot still held 1; slot and buff entity gone 1 step(s) later`, `RemoveBuff of a buff the entity
  does not have: the game answers not removed`, `on a 1_Player entity: listed 1, removed buff 0x339519bd ...`.
- `BuffsOf(entity)` (dev.27): every buff on a player, creature or NPC (`EntityBuff(Id, Slot, Remaining, Level)`), read
  from the entity's BuffContainer by kiln.players (the same read as `PlayerStates.BuffsOf`, which stays). Server only:
  a joined client has no BuffContainer and gets an empty list. Capability `abilities.buff_list`.
- `SequenceOf(actor)` (current sequence id/time/call), `SequenceStartedEvent` / `SequenceEndedEvent` (polled every
  step over the actors kiln.world finds, list refreshed every 50 steps; only while subscribed).
- `Attributes.Get/Max/Add(entity, Health|Mana|Stamina|SpecialAbility)`: the attribute slots through the game's
  attribute code (AttrFind/AttrAdd, formulas re-run; Mana `0x4d7229a8`/`0x6aa2574e`, max `0x17536777`; Stamina
  `0x92410664`/`0x04b6aa8b`, max `0xf443c410`; SpecialAbility `0x9aec7fc4`/`0x046f1fb4`, max `0x9573465b`).

## Kiln natives and new programs
- **Managed natives**: `Abilities.Natives.Register(name, argc, handler)` → `"<modid>.<name>"`. The handler runs on the
  tick thread in the Pre after the ECALL (`NativeInvocation`: `Raw(i)`, `GetInt/GetFloat/GetEntity/GetBytes(i)`,
  `SetInt/SetFloat/SetEntity(i)` through the live instance, `Impact`, `Site`); its return value is pushed when the
  ECALL re-runs in the next step. Lab: `native Double(21) on impact 50331651 … site 0x7e570001` then `kiln.Log: … Out::value
  = 42` and `kiln.Signal tag 7 value 42`. A handler that throws completes the call with error 0x11 (the instance
  stops). `Abilities.Natives.Declare(fullName, argc)` lets a program that calls it pass the verifier first.
- **Built-ins**: `kiln.Log(value)` (one kiln.log line) and `kiln.Signal(value, tag)` → `ImpactSignalEvent`
  (`Tag`, `Value`, `AsInt/AsFloat/AsEntity`, `Impact`, `Site`). Neither suspends.
- **Programs**: ship them as kiln.assets resources (`assets/assets.json`, kind `resource`, type
  `keen::ImpactProgram`, mode `new`; `tools/impact` cooks them). The game fills its ImpactRegistry while the world
  loads (step 2 in the lab); kiln.abilities then verifies and inserts every kiln.assets ImpactProgram that is not in the
  game's own list (the `ImpactRegistryResource` c4933fae-…), and retries programs that wait for natives whenever a
  native is registered or declared. `Abilities.Programs.Register(byte[])` does the same for raw blobs (it returns
  `waiting` while natives or the registry are missing); `Programs.Count/Info/Variables/IdOf/Refresh`. Lab:
  `kiln.abilities selftest: 571/571 programs in the registry pass the verifier (max stack depth 25)` (create
  `kiln/data/kiln.abilities/selftest` to run it).

## Impact program and native contract (runtime side)

`docs/kiln/modding/impact.md`
states the same contract from the tools side (lint codes E_*; the test vectors in `tools/impact/tests/vectors/` give the
runtime verdict of each rule, and the C++ verifier matches all of them).

1. **Packaging.** A program is a plain `keen::ImpactProgram` blob (type hash `0xd4922d88`), exactly the bytes the game
   keeps for a resource: GUID = `programGuid` (+0x04), id (+0x00) = FNV-1a-32 of the 16 GUID bytes (game byte order).
   Mods ship it through kiln.assets: `impact/*.kimp` + `impact/cook.json` are cooked to
   `assets/cooked/impact/<stem>.bin`, listed in `assets/assets.json` as
   `{"kind":"resource","type":"keen::ImpactProgram","part":0,"mode":"new","guid":"…","file":"cooked/impact/<stem>.bin"}`.
   At every server world start kiln.abilities lists the `keen::ImpactProgram` resources kiln.assets serves, verifies
   each GUID the game's ImpactRegistry does not hold yet, and inserts it. `Abilities.Programs.Register(byte[])` is the
   managed path for raw blobs (same verifier). Vanilla GUIDs are never replaced at run time (running instances read
   their program's code live); a boot-time override is kiln.assets' business.
2. **Kiln natives.** Name = `"<modid>.<Name>"` (`modid` as in kiln.mod.json, `Name` = `[A-Za-z0-9_]+`); the ECALL hash
   is FNV-1a-32 over the full UTF-8 name. The engine's registry keeps a 32-byte display name (registerNative traps on
   longer names), so longer names are registered with a truncated display copy; the hash is always over the full name.
   Natives are declared in the mod's `impact/natives.json`
   (`{"format":1,"natives":[{"name","hash","argc","returns","suspends","args":[{"name","type","dir"}],"doc"}]}`,
   args in `args[]` index order). A name whose hash collides with one of the 107 vanilla natives or with another live
   Kiln native is refused (registerNative would overwrite the vanilla native). Built-ins (mod id `kiln`, never
   suspend):
   - `kiln.Log` (`0xc0b137a7`), argc 1: `args[0]` in: any variable handle (logged typed) or a raw integer. Writes one kiln.log line
     (program, impact id, self entity, site). Pushes 1.
   - `kiln.Signal` (`0x38b05baf`), argc 2: `args[0]` in "value": any variable handle (type + up to 64 bytes) or a raw u32;
     `args[1]` in "tag": a raw u32 or a handle to a uint32/sint32 variable. Posts `ImpactSignalEvent` to managed mods
     in the next Pre and pushes 1 at once.

   Managed (C#) natives run on the tick thread: their ECALL suspends (result byte 1) and re-runs the next step, which
   then pushes the handler's value (`"suspends": true` in natives.json; the result is only valid after the resume).
3. **Arguments.** ECALL pops `argc` (≤ 31) values; `args[0]` = the top of the stack (the last value pushed), which is
   the game's own convention (applyDamage `args[0]` = wasBlocked output, `args[1]` = root attacker, `args[2]` =
   target). A value is a variable handle `0xFFFF0000 | index` (resolved through the native context's
   `dataLayout[index].offsetInBytes`), 0 ("not connected") or a raw u32. In the assembler, `ecall Name(a, b, c)` means
   `args[0] = a`. Kiln natives also get the call-site id (native context +0x58, the editor node id; unique per site).
   Result byte: 0 = ok (the value byte is pushed), 1–3 = suspend (the same ECALL runs again next tick), ≥ 4 = error.
4. **Verification before a program is inserted** (lint itself is offline in `tools/impact`):
   - blob arrays and strings inside the blob; id == FNV-1a(GUID);
   - opcodes 1..30 only, operand words inside the array (BR/BRT/BRF/ICONST/LOAD/GLOAD/STORE/GSTORE 1, COPY/CALL/ECALL
     2, the rest 0); jump targets on instruction starts in the same array; code and codeShutdown both end in HALT and
     never fall off the end;
   - stack depth, from pc 0 at depth 0 in both arrays, over the control-flow graph: IADD..ILEQ pop 2 push 1; BR 0/0;
     BRT/BRF pop 1; ICONST/ICONST0/ICONST1/DUP/LTIME/GLOAD push 1 (DUP needs depth ≥ 1); INC/DEC/TIMEFF 1/1; COPY 0/0;
     ECALL pops argc and pushes 1; GSTORE/POP pop 1; RVM/DSELF/HALT 0/0. The depth at a pc must be the same on every
     path, never below 0, and **never above 31**: the instance stack is 32 u32 slots (createImpactInstance allocates
     0x80 bytes) and slot 31 holds the canary `0xBAADF00D`, which the interpreter checks after every run (fatal when
     overwritten). The header's `stackSize` is not trusted;
   - GLOAD/GSTORE operand = `0xFFFF0000 | index`, index < dataLayout count, variable type one of uint32 `0x32940bec`,
     sint32 `0x9859f31e`, float32 `0xe89f7410`, bool `0xc894953d`, Time `0xdfe4e404`; COPY operands are variable name
     hashes of two present variables of the same type; variable `offsetInBytes + size` inside `data`;
   - every ECALL hash is a vanilla native, a live Kiln native or one declared in a loaded mod's `impact/natives.json`,
     with the matching argc (the blob carries no argc);
   - every ECALL site that calls a query native (forEachEntityWithFactionInRange `0x19143416`, destroyFactionQuery
     `0x93e40aa9`, forEachPlayerInRange `0xa8787e70`, destroyPlayerQuery `0x22a62ea4`, entityInRangeWithFaction
     `0x61946152`) is listed in `queryCallIds` (createImpactInstance preallocates query state only for those sites);
   - code and codeShutdown hold at most 65535 words each (pc, sp and branch targets are u16);
   - CALL, RET, LOAD and STORE are refused until their frame semantics are proven at run time (0 uses in the 571
     vanilla programs).

## Events (Kiln's bus, tick thread, Pre)

| Event | When | Writable / cancel |
|---|---|---|
| `ImpactStartingEvent` | a new instance (held one step, see holds) before its first run | variables (`Set`, `Scale`, `Instance`); `Cancel()` destroys its entity |
| `ImpactStartedEvent` | the Pre after an instance was created (configs applied) | observe |
| `ImpactEndedEvent` | an instance reported by Started/Starting no longer exists | observe |
| `ImpactNativeEvent` (`ImpactDamageEvent`, `ImpactBuffEvent`, `ImpactSpawnEvent`) | the Pre after a wrapped vanilla native ran (copy of its args) | observe (rewrite: `Natives.Wrap` / `OnDamage`, job thread) |
| `ImpactSignalEvent` | the Pre after `kiln.Signal` ran | observe |
| `SequenceStartedEvent` / `SequenceEndedEvent` | an actor's `Actor.sequenceRuntimeId` changed | observe |
| `AbilityCastEvent` | a scripted ability passed its checks, before anything is paid | `Cost`, `Reason`, `Cancel()` |
| `AbilityCastedEvent` | the cost was paid, the cooldown started, OnCast is starting | observe |

Events are built only while someone subscribes (the native side collects nothing without interest).

## Native (other services)
`api->service("kiln.abilities", 1)` in `on_ready`. Table: `status`, `set_listener` (one listener: the managed
Kiln.Abilities; it gets every event of the previous step in Pre as an array of 640-byte `KilnAbilityEvent` and returns
its interest mask, `KILN_ABILITY_INTEREST_HOLD` for holds), `hold_filter`, `instance_vars/read/write/info`,
`entity_overrides` (config overrides for the next instance on an entity: by config id, name hash or index),
`program_count/info/vars/register/refresh`, `native_count/at/find/declare/register/unregister/complete/wrap/observe`,
`set_wrap_dispatcher`, `call_original`, `release_owner`, `attr_get/attr_add`, `sequence_state`, `sequence_play`.

How it hooks (all fail soft; `kiln.abilities: natives on, programs on, instances on, attributes on, sequences on`):
- `registerNative` (0x996b80) is detoured at boot to capture the game's 107 natives per registry; at the server
  world's first Pre Kiln re-registers applyDamage/applyBuff/spawnEntity (and any wrapped/observed hash) with its own
  thunks that call the game's function, and registers kiln.Log, kiln.Signal and managed natives in free slots
  (registry capacity 256). Thunks run under SEH: a fault in Kiln's part runs the game's native; a fault inside the
  game's native stops that instance (error 0x1f) instead of the process.
- `createImpactInstance` (0x993eb0): new instances (events, holds, moving entity overrides to the instance).
- `runImpact` (0x99c1e0): first-run overrides, holds, the native registry pointer (EcsIterCtx+0x110).
- The ImpactRegistry map (`*(ImpactSystem+8)`, identity hash, linear probing) for program lookups and inserts; the
  instance pool lookup (0x9932f0); `TriggerSequence` (0x369d0); kiln.combat's AttrFind/AttrAdd.

## Costs and limits
- Without subscribers the hot paths are one atomic load each (createImpactInstance, runImpact, the three wrapped
  natives).
- A hold delays the first run of the held programs' new instances by one step; a managed native delays its ECALL by
  one step per call (a loop over 5 targets takes 5 steps); fire-and-forget work belongs in `kiln.Signal`.
- Programs are verified, never trusted: see the contract. A program is copied into memory that is never freed
  (running instances read their code live).
- Vanilla clients: tooltips and local prediction stay vanilla (a tweaked mana cost or damage shows vanilla numbers in
  the tooltip); new keys need Kiln + the mod on that client.

## Lab test
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/abilities-test/AbilitiesTest.csproj -c Release -o $KILN_OUT/mods/abilities-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any
mkdir -p ~/srv/enshrouded/labs/<lab>/kiln/data/kiln.abilities && touch ~/srv/enshrouded/labs/<lab>/kiln/data/kiln.abilities/selftest
tools/lab/lab.sh start <lab>
grep -a abilities-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log    # ... summary: 73 PASS, 0 FAIL
```
It spawns Vukahs and explosions 40+ m from the copied world's flame altar and removes them.

The key path without a client: with `"labPort"` in the lab's `kiln/config/network.json`, a fake Kiln client
(tools/nettest's `Client`) that sends `CastRequest("firenova:fire-nova")` on `kiln:abilities` gets
`CastReply ok False reason 'no caster'` (the fake peer has no player entity) and `unknown ability 'nope:unknown'` for
an unknown id: request, server validation and reply work; the cast itself needs a player. In a client the
same mod only registers `/abt status|mana|day|night|vukah|boom|near|seq` for manual checks.

## In a client
Client 26404966 hosting the LOCAL slot 1 character's world offline (`saves: world loaded: slot 0x0002 (world1.ksd)`),
Kiln with FireNova, abilities-test (session commands through `kiln/control/console`, since offline sessions have no
chat) and impactsample.
- **On a client host**: the sigdb resolves unchanged, `the game's ImpactRegistry holds 571 programs (step 2); 3 Kiln
  program(s) from kiln.assets registered` (impactsample's), `instance layout ok`, impactsample's own checks PASS.
- **Key G**: `SESSION cast firenova:fire-nova by 1 (Key), paid 40 mana`, the ring of 7 Fireball explosions on screen,
  the HUD line `Fire Nova [G]: 4.5 s`, a second press gives the toast `Fire Nova: cooldown 3.8 s`. The console
  command `nova` casts too (`… (Command), paid 40 mana`; `/cast` right after: `busy casting`).
- **Damage and credit**: Kiln-spawned explosions collide and credit the root player
  (`applyDamage … attacker 1 target 6976 … result 1`, `damaged 6976 by 1`). Without a `Damage1` config they hit
  for **0** (a Kiln impact has no used item; a staff cast takes its damage from the spell's damageSetup), so FireNova
  passes `Damage1 = FireOnly(10)`: 16 per explosion on a Vukah (85→69→53→29, one crit).
- **Holds do not break collisions**: FireNova holds program 0x7a120ee8 for `ImpactStartingEvent`, and the held
  explosions still hit.
- **Per-cast tweak at night**: the same explosion `AttackMod 2 … damaged 7103 by 1: 111->79` (32) at 23:00 vs 16 at
  12:00.
- **ApplyBuff on the player**: the HUD shows "Healing Over Time" with its VFX and the health bar rises.
- **PlaySequence on the player**: the server sets the player's `Actor.sequenceRuntimeId` to the Fireball cast for
  about 10 steps, then it is cleared: no animation, no mana cost. Cause (docs/re/abilities.md §9):
  the cast's KeepAliveEvent at 0.2 s needs the held-input state InRangeCombat; `HoldInputs` (now the default) holds
  it. Emotes and melee attacks never needed it.
- Enemy attacks show in the same events: the Vukah's swing program 0c3a7176 `applyDamage … attacker 6976 target 1`.

## Client checklist (needs a joined or hosting player)
1. Fire Nova on G: mana −40, the ring of the game's Fireball explosions (VFX on a vanilla client too), enemies in the
   ring take damage credited to the caster (XP, the damage numbers), the caster is not hit; G again within 5 s: the
   "cooldown" toast; `/nova` from chat (also from a vanilla client).
2. Held instances and collisions: with a `ImpactStartingEvent` subscriber (FireNova holds program 0x7a120ee8), do a
   held explosion's first-step collision events still arrive (the enemy takes damage)? If not, per-cast damage tweaks
   belong in `Abilities.OnDamage` (synchronous, no hold).
3. At night FireNova doubles the Fireball explosions' AttackMod: compare the damage numbers with day.
4. `PlaySequence` on a player (`/abt seq greet|dance|fists|whirl|fireball|bow [nohold] [queue]`): done for the
   player's own vanilla client (emotes, whirl, held cast pose; above). Open: a second client watching, and a
   cast that fires the equipped spell (needs the used item in the trigger context).
5. A Kiln-spawned `Explosion_Player_Spell_Fireball_1` next to an enemy near the player (`/abt near`): damage, burn,
   kill credit to the root player.
6. impact-asm's ImpactSample: chain_explosion credited to the player, heal_over_time, signal_projectile.
