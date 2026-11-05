# Impact programs: new and changed gameplay logic

Every spell, weapon attack, perk, buff, skill node and enemy attack in Enshrouded runs an **Impact program**: a small
bytecode program on a stack VM. Its instructions call the game's C++ natives (applyDamage, applyBuff, spawnEntity,
changeAttributeValue, …). With `tools/impact` a mod can:
- read any vanilla program as text;
- write a new program from scratch;
- clone a vanilla program under a new GUID and change it (constants, config bindings, a native swapped, code
  inserted);
- call **Kiln natives**: functions a mod implements in C#, through kiln.abilities.

Programs run **on the server only**. Vanilla clients are fine as long as the program spawns templates and buffs that
they know.

Before writing a program, check whether a config value does the job: damage, radius, durations, buffs, spawned
templates and costs are configs or resource fields (data packs). Programs are for new
*behaviour*.

Evidence and VM semantics: `docs/re/impact.md`. Runtime side: `docs/kiln/services/abilities.md` (kiln.abilities).

Status:
- **Offline:** proven. All 571 vanilla programs disassemble, re-assemble byte for byte and pass the verifier.
- **Lab, no player:** proven. A clone with inserted code runs in the game, through a kiln.assets override and
  through kiln.abilities' registration. ImpactSample's `signal_explosion` and `chain_explosion` pass.
- **Still open:** the heal-over-time buff needs a BuffType bound to it (a data-pack section), and the projectile
  variant needs a real client.

## Quick start
```sh
I=tools/impact/impact.py                     # Python 3.14 (like tools/kfc); reads the installed game read-only
python3 $I natives                           # the 107 vanilla natives + Kiln's built-ins, args in args[] order
python3 $I natives --show applyBuff          # one signature with types, directions and notes
python3 $I dis 80a2448f-945c-45f6-8fce-9b09ed61620c     # a vanilla program as text (the Fireball I explosion)
python3 $I asm my.kimp -o my.bin             # assemble + lint (no game files needed)
python3 $I lint my.bin                       # the verifier kiln.abilities applies
python3 $I cook mods/MyMod/impact/cook.json --update-manifest   # build a mod's programs
python3 $I roundtrip                         # local check: all 571 vanilla programs re-assemble byte for byte
```
Default game folder: Steam's Enshrouded (`--game DIR`); outputs: `$KILN_OUT` (default `<repo>/.out/impact`).

Vanilla listings are game data. Read them, but never commit them or ship them in a mod: ship a clone recipe instead
(below).

## The language (.kimp, format 1)
```
.kimp 1
program aad149f6-5485-4607-875e-62722822c77f     ; the program's GUID, fixed forever (python3 -c "import uuid; print(uuid.uuid4())")
stack 256                                        ; header field (vanilla: 256)
streams none                                     ; event streams the instance allocates: OnCollision, OnHit, OnParry

var $target   ecs.EntityId                       ; variables: $name type [@offset] [config id] [= initial value]
var $amount   sint32 config "impactsample.HealPerTick" = 4
var $interval Time   config "impactsample.HealInterval" = 1s

code:
        ecall getBuffTarget($target)             ; args[0] = $target
        pop
tick:   ecall changeAttributeValue($health, $add, $old, $target, _, $amount) site heal
        ...
shutdown:                                        ; runs when the instance ends (default: halt)
        halt
```
`;` starts a comment.

### Header
| Line | Meaning |
|---|---|
| `program <GUID> [id 0x…]` | The id defaults to FNV-1a of the GUID's 16 bytes, which is how the game finds programs. Never set it by hand. |
| `stack <n>` | The header's stackSize. The real instance stack is 32 slots regardless. |
| `streams …` | Allocates the event buffers onEvent needs: OnCollision for projectiles and AoE volumes, OnHit for hit reactions. |
| `data_size <n>` | Only when the data block must be larger than the variables need. |
| `queries auto` | The default: `queryCallIds` = the call sites of the query natives (forEachEntityWithFactionInRange, forEachPlayerInRange, entityInRangeWithFaction and their destroy calls). |

### Variables
`var $sym <type> [@offset | @$other+n] [size n] [config C] [name 0x…] [dbg "…"] [= value]`
- **type**: an impact type name. `python3 $I natives --show …` lists what each native takes. The common ones:
  - numbers: `float32`, `sint32`, `uint32`, `bool`, `Time` (ns inside; GLOAD gives ms), `float3`;
  - entities and transforms: `ecs.EntityId`, `WorldTransform`;
  - references: `ecs.TemplateReference`, `BuffTypeReference`, `AttributeReference`, `MaterialFeedbackId`;
  - masks and enums: `FactionMask`, `impact.DamageSetup`, `impact.HitReactionType`, `impact.PushBackForce`,
    `impact.AttackCategoryMask`, `impact.EventStream`, `impact.EventStreamType`,
    `impact_nodes.ValuesMathOperator`.
  - The full list is in `tools/impact/data/types.json`. A type's hash is FNV-1a of its name.
- **offset**: by default the next 16-byte boundary. `@$Damage+12` places a variable inside another one, e.g. one field
  of a DamageSetup.
- **config**: the binding (template `ImpactConfiguration`, item `impactValues`, buff, sequence event, skill node)
  writes its value of that config id into the variable when the instance starts.
  - Forms: `config 0x72864524`, `config Damage` (an alias from the vanilla vocabulary, `tools/impact/data/configs.json`),
    or `config "mymod.Radius"`. A string means FNV-1a of the string.
  - A new config id is set by your data pack with the same id.
- **values**:

  | Form | Example |
  |---|---|
  | number | `2.5`, `-3`, `0x10` |
  | bool | `true` |
  | time | `250ms`, `1.5s` |
  | enum | `Medium` |
  | mask | `[Vukah, Player]` |
  | struct | `(1, 0, 0)`, `{magicalFireDamage: 12}` |
  | GUID | `31e7c423-2c04-44ef-81ff-584d72fe6c28` |
  | raw bytes | `hex:0000803f` |

- **name / dbg**: kept only so that vanilla listings re-assemble exactly. Leave them out in your own code.

### Code
| Instruction | Effect |
|---|---|
| `iconst $v` / `iconst 5` / `iconst0` / `iconst1` | push a variable handle / a raw value / 0 / 1 |
| `gload $v` / `gstore $v` | push / pop a uint32, sint32, float32 (bits), bool or Time (in ms) variable |
| `iadd isub imul idiv ilt ieq ileq` | 32-bit integer math and compares (signed) |
| `inc dec dup pop` | |
| `br L` / `brt L` / `brf L` | jump / pop and jump if non-zero / if zero |
| `ltime` | push now (ms) |
| `timeff` | float32 seconds on top → ms |
| `rvm` | yield: continue at the next instruction in the next simulation step |
| `dself` | mark the program's entity for destruction (execution continues) |
| `halt` | end the program |
| `copy $a, $b` | copy variable a into b (same type) |
| `ecall name(a0, a1, …) [site s]` | call a native (below) |
| `word 0x…` | raw words (for listings of broken code only) |

Rejected by the verifier: `call`/`ret`/`load`/`store`. Vanilla never uses them.

**Floats:** the integer instructions do not do float math. For float math use the natives `add`/`sub`/`mul`/`div`
(`ecall mul($result, $op, $a, $b)` with `$op impact_nodes.ValuesMathOperator = Mul`), and `less`/`equal` to compare.

### Calling natives
`ecall name(a0, a1, …)` lists the arguments **in args[] order**, the order `natives --show` prints. The assembler
pushes them last to first, so a0 is pushed last and is `args[0]`, the top of the stack.
- **Arguments:**
  - `$var`: a variable handle. Natives read and write variables, never stack values.
  - `_`: unconnected. Optional inputs fall back to defaults; a required input makes the native fail (NoConnection).
  - An integer: only where a native declares raw values (`kiln.Log`, `kiln.Signal`). Vanilla natives would read
    "variable #n".
- **Result:** every native pushes a result byte. Pop it, or branch on it: `brt`/`brf` for the boolean natives (`isEnemy`,
  `hasBuff`, `randomBranchNode`, the `forEach*` loops: 1 while there is an element).
- **Sites:** each call has a site id that natives key their per-call state on. A pair that must share state (an
  applyAttributeValue in `code` and its revertApplyAttributeValue in `shutdown`; a query and its destroy) uses the
  same `site name`.
  - `site <name>` = FNV-1a(program GUID ‖ name).
  - Without a site, each call gets a fresh id.
  - Clones get fresh ids that keep their pairs: FNV-1a(new GUID ‖ old id).
- **Names:** `ecall applyDamage`, `ecall ApplyDamage` (the node name) and `ecall 0xb04d0e7a` are the same native. An
  undeclared name needs `argc n`.

### Idioms (as the game's compiler writes them)
```
; wait $t (a Time variable)
        gload $t
        ltime
        iadd                 ; end = now + t, kept on the stack
w:      dup
        ltime
        ileq                 ; end <= now ?
        brt go
        rvm
        br w
go:     pop

; for each collision of this step (usedStreams OnCollision)
        ecall onEvent($stream, $type)            ; $type impact.EventStreamType = OnCollision
        pop
        iconst0
        gstore $i
next:   ecall forEachCollisionInStream($other, _, $pos, _, _, _, _, _, _, $i, $stream)
        brf done
        ...                                       ; use $other / $pos
        br next
done:
```

## Rules the verifier enforces
kiln.abilities refuses a program that breaks a rule marked [rt]. `impact.py lint` shows all of them. Details:
docs/re/impact.md §6.
- **Stack depth ≤ 31** at every instruction, and the same depth on every path [rt]. The instance stack has 32 slots;
  the last holds a canary, and overwriting it is fatal for the server.
- No path may run past the end, and `code` and `shutdown` each end in `halt` (add one after a final `br`) [rt].
- Branch targets must be instruction starts [rt].
- **Natives:** every called native must be known (vanilla, Kiln built-in, or declared by your mod), with its argc [rt].
- **Variables:** gload/gstore only on uint32/sint32/float32/bool/Time variables [rt]; handles in range [rt].
- **Query sites:** every query native's site is listed in queryCallIds (`queries auto` does it) [rt].
- No raw integers for vanilla natives' arguments.
- **Unconnected arguments:** some natives read an argument with no check, so leaving it unconnected kills the server (`hasAttackCategory`'s
  `matchAll`, `isEnemy`'s second entity) [rt]: the linter says `E_UNCONNECTED`. An argument every vanilla call connects but the native does
  check is a warning (`W_UNCONNECTED`).
- **No step limit:** a loop without `rvm` or a native that ends it hangs the server's simulation step. The linter
  warns (W_LOOP).
- **Program code is read live by running instances.** kiln.abilities refuses to replace a registered program. Always
  clone to a new GUID.

## Clone and patch: change a vanilla program
A mod never ships vanilla code. It ships a **recipe**: the vanilla GUID plus edits. The cook applies them to the
player's own copy. Edits (in `cook.json` `clone` steps, or `impact.py clone --guid NEW --edits edits.json`):

| Edit | Example |
|---|---|
| initial value | `{"set": "$NoiseRange::value", "value": "40.0"}` |
| config binding | `{"config": "$NoiseRange::value", "id": "mymod.Noise"}` (`"0"` unbinds) |
| new variable | `{"var": "var $tag uint32 = 7"}` |
| swap a native (same argc) | `{"swap": "triggerNoise", "with": "kiln.Signal", "nth": 0}` (`"all"` for every call) |
| insert code | `{"insert": "ecall kiln.Log($x)\npop", "after": {"native": "getSelf"}}` or `{"insert": {"file": "x.kpatch"}, "before": {"pc": 134}}` |
| replace a call statement | `{"replace": {"native": "triggerNoise"}, "code": "…"}` |
| remove a call statement | `{"remove": {"native": "getSelf"}}` (only when its result is popped) |
| header | `{"streams": "OnCollision"}`, `{"stack": 256}` |

- **Anchors:** `{"native": n, "nth": k, "section": "code"}` is a call statement: its argument pushes, the ecall and a
  following pop. `{"pc": n}` is an instruction of the *base* program (pcs from `impact.py dis`). `{"label":
  "start"|"end"}` also works.
- **Jumps:** every jump is relocated. Jumps that targeted an anchored statement enter code inserted `before` it.
- **Snippets:** inserted code uses the program's `$variables` (the names `dis` shows) and its own local labels. A label
  on the last line means "the code after the insert".

## Kiln natives (the contract with kiln.abilities)
kiln.abilities states the same contract from the runtime side in docs/kiln/services/abilities.md.
- **Name:** `"<modid>.<Name>"`: the mod id from kiln.mod.json, then Name `[A-Za-z0-9_]`. Mod id `kiln` is reserved
  for Kiln's built-ins. The game keeps a display copy of at most 32 bytes; longer names are truncated for display only.
- **Hash:** FNV-1a-32 over the full UTF-8 name. This is the value in the bytecode (vanilla natives hash their node
  name the same way: FNV("ApplyDamage") = `0xb04d0e7a`). A hash that collides with one of the 107 vanilla natives
  or another Kiln native is refused by lint and by kiln.abilities: registering it would replace the vanilla native.
- **argc:** 0..31. It comes from the registration or the declaration, never from the bytecode.
- **Argument order:** args[0] is the value pushed last. `ecall m.N(a, b)` passes a as args[0].
- **Values:** u32s. A variable handle `0xFFFF0000 | index`: resolve it through the program's dataLayout and the
  instance data. 0 means unconnected. A raw integer is allowed only where the declaration says `"raw": true`.
- **Site id:** the ECALL's first operand. Kiln natives get it as `NativeCall.Site`.
- **Result:** error byte 0 = ok, and the value byte is pushed. 1–3 = suspend: the same ECALL runs again next step with
  the same arguments. ≥ 4 = error, and the instance stops. Managed natives run on the tick thread: their ECALL
  suspends once and the result arrives one step later (`"suspends": true`).
- **Declaration** (`impact/natives.json`, read by the assembler, the linter and kiln.abilities):
  ```json
  { "format": 1, "natives": [
    { "name": "mymod.Chill", "argc": 2, "returns": "1", "suspends": true,
      "args": [ { "name": "target", "types": ["ecs.EntityId"], "dir": "in" },
                { "name": "seconds", "types": ["float32"], "dir": "in" } ],
      "doc": "Applies the chill buff to target." } ] }
  ```
  The C# side registers the same name, e.g. `Abilities.Natives.Register("Chill", argc: 2, handler)`; a name without
  the `<modid>.` prefix gets the calling mod's id.
- **Built-ins** (mod id `kiln`; neither suspends; both push 1):
  - `kiln.Log(value)` (`0xc0b137a7`): writes a kiln.log line with the program, instance, entity, site and the value
    (typed by its variable, or a raw integer).
  - `kiln.Signal(value, tag)` (`0x38b05baf`): posts `ImpactSignalEvent {Tag, Value, ImpactId, Program, Self, Root,
    Site}` to managed mods in the next Pre. The tag is a raw u32 or a uint32/sint32 variable. Same arity as
    triggerNoise.

## Packaging (one pipeline: kiln.assets)
```
mods/MyMod/
  kiln.mod.json                side "server" (programs run on the server); clients "none" unless you add templates
  impact/
    heal_over_time.kimp        your programs (sources, committed)
    chain.kpatch               code you insert into clones (committed)
    natives.json               Kiln natives your programs call (committed; your C# registers them)
    cook.json                  the recipe (committed)
  assets/
    assets.json                kiln.assets entries for the cooked programs (committed)
    cooked/impact/*.bin        made by the cook on the player's machine; gitignored, never shipped
  .gitignore                   assets/cooked/
```
`impact/cook.json` follows the `cook.json` convention of docs/kiln/modding/models.md (format 1). It is plain JSON: no
comments, no trailing commas (`kiln lint` reports KL002 and KL003).
```json
{
  "format": 1,
  "tool": "impact",
  "out": "../assets/cooked/impact",
  "manifest": "../assets/assets.json",
  "natives": ["natives.json"],
  "game": { "side": "server", "madeOn": "1285601" },
  "inputs": {
    "fireball_explosion": { "guid": "80a2448f-945c-45f6-8fce-9b09ed61620c", "type": "keen::ImpactProgram" }
  },
  "steps": [
    { "op": "assemble", "src": "heal_over_time.kimp", "stem": "heal_over_time" },
    { "op": "clone", "base": "fireball_explosion", "guid": "7e24a487-bff5-4bae-aefc-b273059dc90f",
      "stem": "chain_explosion", "edits": [ { "set": "$NoiseRange::value", "value": "40.0" } ] }
  ]
}
```
- `out`: relative to cook.json.
- `game`: informational.
- `inputs`: vanilla programs to clone, read from the installed game.
- `edits`: the clone's changes (the table of edits above).

`python3 tools/impact/impact.py cook mods/MyMod/impact/cook.json --update-manifest`:
1. assembles and clones;
2. lints every program (a program with errors is not written);
3. writes `assets/cooked/impact/<stem>.bin`;
4. adds the matching `assets.json` entries;
5. writes `cook-report.json`: the recipe, source, input and output hashes, the game build, each program's lint
   result and the natives it needs.

The entry for one program:
```json
{ "kind": "resource", "guid": "<program GUID>", "type": "keen::ImpactProgram", "part": 0, "mode": "new",
  "file": "cooked/impact/heal_over_time.bin" }
```
- `--check` says whether a cook is needed (recipe, sources or outputs changed).
- A game update that moves or changes a cloned input stops the cook with a clear error.

**Registration.** kiln.assets serves the blobs. At the first Pre of every server world, kiln.abilities registers every
*new* `keen::ImpactProgram` resource into the game's ImpactRegistry after the [rt] checks. From then on the game finds
the program by its GUID like a vanilla one.
- Code can register raw blobs with `Abilities.Programs.Register(byte[])` (same checks).
- A `mode: "override"` entry replaces a vanilla program's bytes at boot through kiln.assets. That is allowed, but a
  clone under a new GUID is the safe way.

**Binding a program to gameplay.** A program does nothing until something starts it:
- a template's `Impact.program` (projectiles, explosions, zones);
- a BuffType's `impact`;
- a sequence's `SpawnImpact` event;
- an item's equipment program;
- a perk or a skill node.

These are data-pack fields (kiln.content's template, buff, sequence, perk and skill sections). From C#,
`Abilities.RunProgram(programGuid, position, configs, root)` spawns a generic impact entity that runs it.

## The sample: `mods/samples/ImpactSample`
| File | What |
|---|---|
| `impact/heal_over_time.kimp` | a buff program written from scratch (example 2) |
| `impact/chain.kpatch` + the `chain_explosion` step | a Fireball explosion clone that chains (example 1) |
| `signal_explosion` step | the explosion with `kiln.Signal` and the mod native `impactsample.Ping` after its opening `getSelf` (player-free proof) |
| `signal_projectile` step | the Fireball projectile with `triggerNoise` replaced by `kiln.Signal` (client checklist) |
| `impact/natives.json` | declares `impactsample.Ping` (argc 1, a managed native) |
| `ImpactSampleMod.cs` | registers Ping, logs the signals, and when the world runs: runs `signal_explosion` and `chain_explosion` (next to a Vukah it spawns) and logs `impactsample: PASS …` / `FAIL …` |
| `build-assets.sh` | cooks the programs (`GAME=` a client or dedicated-server folder) |

Build and try it:
```sh
mods/samples/ImpactSample/build-assets.sh
dotnet build mods/samples/ImpactSample/ImpactSample.csproj -c Release --artifacts-path $KILN_OUT/dotnet -o $KILN_OUT/mods/impactsample
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any      # copies $KILN_OUT/mods/* into the lab
```

## Example 1: a fireball that chains to a second target
`mods/samples/ImpactSample` clones the Fireball I explosion program (`80a2448f`) as `chain_explosion` (GUID
`7e24a487-…`).
- **Where:** right after the explosion's damage loop (base pc 134), inserted code (`impact/chain.kpatch`) looks for
  another enemy of the caster within 8 m. It skips the target the blast just hit.
- **What:** it spawns a vanilla Fireball I explosion on that enemy, whose own program does not chain again.
- **Then:** `kiln.Signal` reports it (tag `0xC4A1`), and the query is released.

```
        iconst0
        gstore $Chain.index
next:   ecall forEachEntityWithFactionInRange($Chain.entity, $Chain.transform, $Chain.index, $Chain.factions, $impact_nodes.GetTransform::transform, $Chain.range) site chain
        brf none                 ; no (more) candidates
        ecall isSameEntity($Chain.entity, $impact_nodes.ForEachCollisionInStream::otherEntity)
        brt next                 ; the target this explosion just hit
        ecall isEnemy($impact_nodes.GetRoot::entityId, $Chain.entity)
        brf next                 ; not an enemy of the caster
        ecall spawnEntity($Chain.spawned, $Chain.breakHierarchy, $Chain.despawnLink, $Chain.copyLevel, $Chain.copyFaction, $Chain.template, $Chain.transform, _, _, _, _, _, _)
        pop
        ecall kiln.Signal($Chain.entity, 0xC4A1)
        pop
        ecall destroyFactionQuery($Chain.entity, $Chain.transform, $Chain.index, $Chain.factions, $impact_nodes.GetTransform::transform, $Chain.range) site chain
        pop
none:
```
- **Variables:** the recipe adds the `$Chain.*` variables with `var` edits. The range, factions and template are
  config-bound (`impactsample.ChainRange`, `impactsample.ChainFactions`, `impactsample.ChainTemplate`), so a data
  pack can tune them.
- **Hierarchy:** `breakHierarchy false` keeps the caster as the hierarchy root, so the chained hit is credited to
  the player.
- **Query release:** `destroyFactionQuery` uses the same `site chain` as the query, which releases it early.
- **Making the staff use it:** a template clone of `Explosion_Player_Spell_Fireball_1` (`31e7c423-…`) whose
  `Impact.program` is the chain program, and the Fireball projectile's `SpawnTemplate` config pointing at that clone.
  Both are data-pack fields; vanilla clients then need the new template (a Kiln client with the synced definitions).

## Example 2: a buff that heals over time
`impact/heal_over_time.kimp` is written from scratch (`aad149f6-…`):
```
var $target    ecs.EntityId
var $health    AttributeReference = 61f22138-a061-4b3f-a15e-35953e731112   ; Health (attribute id 0x8eb84995)
var $add       impact_nodes.ValuesMathOperator = Add
var $old       sint32
var $amount    sint32 config "impactsample.HealPerTick" = 4
var $interval  Time   config "impactsample.HealInterval" = 1s

code:
        ecall getBuffTarget($target)
        pop
tick:   ecall changeAttributeValue($health, $add, $old, $target, _, $amount) site heal
        pop
        gload $interval
        ltime
        iadd
wait:   dup
        ltime
        ileq
        brt again
        rvm
        br wait
again:  pop
        br tick
        halt                     ; never reached; every code array ends in halt
```
- **Running it:** a BuffType whose `impact` is this program runs it on the buff's entity for the buff's lifetime. It
  ends with the buff, so the loop needs no exit.
- **Health:** an `sint32` attribute; `changeAttributeValue` adds `$amount` each `$interval`.
- **Tuning:** both values are config-bound, so the buff's `impactValues` (or an ApplyBuff event) can set them per
  buff.

## Testing
- **Offline:** `impact.py lint`, `impact.py cook`, and `python3 -m unittest discover -s tools/impact/tests` (synthetic
  only).
- **Local check against the installed game:** `impact.py roundtrip`. Every vanilla program must round-trip, and
  listings go to `$KILN_OUT/dis/` (do not commit them).
- **Lab, no player needed:** `tools/impact/lab/run.sh <lab>` cooks a boot-time override of the Fireball explosion
  program and deploys the ImpactLab check mod (`impact-lab: PASS override program ran` in kiln.log). It proves that
  the game loads a tools/impact blob through kiln.assets and runs it (docs/re/impact.md §7).
- **Lab, with kiln.abilities:** ImpactSample's `signal_explosion` is the Fireball explosion with `kiln.Signal` right
  after its opening `getSelf`. Run it with `Abilities.RunProgram` and watch for `ImpactSignalEvent`.
  - Explosions run their opening code at once on spawn, without a player.
  - Collisions need a player near (docs/re/abilities.md §3 lab limits).
- **In a client:** `signal_projectile` replaces the Fireball projectile's `triggerNoise` with `kiln.Signal`. It
  signals the impact position when a real cast hits something.
