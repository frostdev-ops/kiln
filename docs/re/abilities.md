# Spells, abilities, skills, buffs and animations: engine evidence (svn 1285601)

Builds: dedicated server Steam 26404334 / svn 1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`), client Steam
26404966 (`enshrouded.exe` sha256 `ce6fdba6…`), image base `0x140000000`, RVAs below (server unless marked "client").
Sources: EMF 0.3.3 docs (MIT: `ENGINE_MAP.md`, `engine_parts/plumbing.md` §5, `combat.md` §2/§5, `ecs_programs.md`);
the game's own reflection and resources read offline with `tools/kfc` (Kiln, MIT; the client exe's 12,658 types);
Ghidra 12.1.2 headless decompiles (no auto-analysis) and capstone; the lab `abilities` (a copied world, no
player, port 26085) with a probe mod; runtime notes from the creatures-research worker. From kfc-parser (GPL-3) only one
documented fact was used: an Impact program has code, shutdown code and data. Its code was not read.

Evidence tags (all but `[hyp]` count as proven):
- **[dis]** disassembly/decompile;
- **[refl]** game reflection;
- **[data]** decoded game resources;
- **[rt]** our lab;
- **[peer-rt]** creatures-research's lab;
- **[emf]** EMF statement, not re-checked;
- **[hyp]** hypothesis.

## Short answers
- **Everything is data plus one VM.** Spells, weapon attacks, abilities, perks, buffs, skill-tree nodes and enemy
  attacks are all built from the same three things:
  1. **actor sequences** (`keen::actor::ActorSequenceResource`): timed events. The animation state, VFX/SFX, the
     spawn of a projectile or impact, cost payment, ability locks, buffs, impulses.
  2. **entity templates** (`keen::ecs::TemplateResource`): projectiles and AoE volumes with an `ecs::Impact` component.
  3. **Impact programs** (`keen::ImpactProgram`): u32 bytecode on a 30-opcode stack VM that calls 108 C++ natives
     (ApplyDamage, ApplyBuff, SpawnEntity, …).

  Every binding passes **typed config values** (`keen::impact::Configurations`) into the program's variables by
  config id. So almost every number (damage, mana cost, radius, duration, buff, spawned template) is a config value or
  a field, not bytecode. **[refl] [data] [dis]**
- **The server runs it all.** The Impact VM, hits, costs and buffs run in the server world. The dedicated server has
  every gameplay resource **byte-identical** to the client, including all 5,461 animation clips and 278 animation
  graphs. It lacks only VFX, sound, render models and UI textures. **[data] [rt]**
- **Clients render from their own copies.** They see templates by GUID, animation states by id, buffs by id, and their
  own tooltips (`uiValues`). Server-only number changes work with vanilla clients; their tooltips and local
  prediction show vanilla.
- **All 571 programs disassemble cleanly.** With the opcode table recovered from the interpreter, every program decodes
  with 0 unknown opcodes, and all calls resolve to the 108 registered natives. **[dis] [data]**
- **Every ability resource re-encodes byte for byte** with `tools/kfc` (decode → encode): programs, sequences, templates,
  items, perks, the buff and skill collections, the registries. **[data]**
- **New natives can be registered.** `registerNative` inserts into or overwrites a hash map that has room (107 of 256
  slots used at run time). Programs reach a native only through its name hash, so a new native needs bytecode that
  calls it. **[dis] [rt]**
- **No player cooldown system exists.** "Cooldowns" are sequence timing (ability masks re-enabled at set times) plus
  costs (mana, stamina, special-ability charge, item charges). Only enemies have `enemy01::AttackCooldownGroup`.
  **[refl]**

## 1. How behaviour is bound: program + configs

| Binder | Program field | Values field | Where | Tag |
|---|---|---|---|---|
| entity template (projectile, AoE, zone) | `ecs::Impact` (24 B: `program` ObjectReference, `programId`, `impactId`) | `ecs::ImpactConfiguration` (72 B: `configValues`, `damageDistribution` + `IsSet`) | template components | [refl] [data] |
| item | `ItemInfo.equipment.program` (+0x104+0x34), `equipment.impactEntity` (template, +0x24) | `ItemInfo.impactValues` (+0x5F8), `damageSetup` (+0x67C), `perkReferences` (+0x608) | ItemInfo (2088 B) | [refl] |
| sequence event `actor::SpawnImpact` (488 B) | `impact` (+0x148) | `impactValues` (+0x1D4), `damageDistribution` (+0x168), `colliderData` | ActorSequenceResource | [refl] [data] |
| perk `keen::Perk` (204 B, 365 resources) | `impact` (+0x30) | `impactValues` (+0x4C), `damageModifier` (+0x5C), `perkArmorSetup`; `simpleAttributeBuff` | Perk | [refl] [data] |
| buff `keen::BuffType` (184 B, 274 in one `BuffTypeCollectionResource`) | `impact` (+0x4C) | `impactValues` (+0x60), `defaultLifeTime`, `slot`, `applyType`, `factionExcludeMask` | collection 02022e86-… | [refl] [data] |
| skill node `keen::SkillTreeNode` (140 B, 243 in one `SkillTreeResource`) | `impactReference` (+0x4C) | `configValues` (+0x64), `costs`, `maxLevel`, `activeSkillUnlock` (+0x18 ItemReference) | 108ac23f-… | [refl] [data] |

- `impact::Configurations` = `{simple, scaled}`, each a `BlobArray<BlobVariant<impact::ImpactConfig>>`. Variant
  types include Float, Sint32, Uint32, Bool, Time, Float2/3/4, Template, Buff, Item, ItemId, Material, Faction,
  FactionMask, HitReactionType, PushBackForce, MajorDirection, Skill, WeaponCategory, EntityTag, AttackCategoryMask,
  AttributeReference and DamageTerraformingSetup, plus Scaled* (`scaleFactor`, `function` None/Linear/Percentage/
  WeaponPowerCreep, `source` Self/Parent/Root/Weapon + attribute). Each carries `configId` (HashKey32). **[refl]**
- The program side: `ImpactVariable` (24 B: `name`, `configId`, `type`, `size`, `offsetInBytes`, `dbgName`). A
  variable with a non-zero `configId` gets the binding's value of that id when the instance starts. **[refl] [dis]**
- **Config vocabulary.** 571 programs, 13,850 variables, 2,979 config-bound, only **346 distinct config ids**, which
  programs share:

  | Config id | Variable name | Used by |
  |---|---|---|
  | `0x72864524` | Damage / DamageSetup | 100 programs |
  | `0x5adb2644` | BuffType | 68 |
  | `0x130a0ef2` | Duration / DebuffDuration | 25 |
  | `0x242c6bbe` | AttackCategory | 104 |
  | `0x5333fdf6` | DamageScaleAttribute | 77 |
  | `0xbf09083f` | Material | 138 |
  | `0xf6a785cd` / `0x29aa2976` | SpawnTemplate / SpawnTemplate2 | |
  | `0xa19409b2` | NoiseRange | |

  The full map (scratch): `abil/cfgindex.json`. **[data]**
- **Ids are FNV-1a-32 of the GUID's 16 bytes** (game order = .NET `ToByteArray`), the same rule as ItemId:
  - ProgramId: `bae6dc9c-…` → `0x1d4402ef`. impact_initialize computes it with `0x40720` when the component's id is 0.
  - SkillNodeId: `dc0e7a77-…` → `0xffd5f940`.
  - Animation StateIdHash: `bcde8195-…` → `0xa34996b4`.

  **[dis] [data] [rt]**

### Binding at instance start (`impact_initialize` 0x197250, client 0x3f5ed0) **[dis]**
For every entity whose `ecs::Impact` has no instance yet:
1. programId = FNV(GUID) if 0. `getProgram` (0x992f80): `ProgramId → ImpactProgram*` map in the ImpactRegistry (bits
   `+0x70`, cap `+0x78`, keys `+0x90`, values `+0xa8`, count `+0xbc`).
2. `createImpactInstance` (0x993eb0). Add `ImpactInitialized` (`0xadf19f25`).
3. Apply configs with `0x98c7f0(system, impactId, Configurations*, dataLayout)`, in this order:
   - the entity's `ImpactConfiguration`, plus its `damageDistribution` when set (`0x190e50`);
   - `BuffComponent` → `getBuffType` (0x933710) → `BuffType+0x60`;
   - `ImpactDirection`;
   - `SequenceEventImpactConfig` (sequence id, sub-sequence, event GUID) → that event's `impactValues` (`0x98ca50`,
     which also sets config `0x220e6b10` "UsedItem");
   - `UsedItem` → ItemRegistryFind → item config (`0x191c10`);
   - `SkillIdComponent` → skill node `+0x64`;
   - `ImpactItemConfig` (used item + ammunition, `0x1933b0` twice) → `ItemInfo.impactValues`.

Values are copied **once per instance**, so a changed config applies to the next cast, projectile or buff, not to
running ones. A type mismatch logs `[impact] Config Variable '%k' type did not match. Expected '%s' got '%s' …`.

### Runtime (lab `abilities`, probe detours on getProgram and the interpreter) **[rt]**
```
abilities-probe: ImpactRegistry f08201f0: count(+0xbc) = 571
abilities-probe: idle world (75 steps after Run): interpreter calls 74, distinct ProgramIds resolved 2: 0xd38d5f4dx20941 0xf5f9de3ex151
abilities-probe: after Explosion_Player_Spell_Fireball_1: ... distinct ProgramIds resolved 3: ... 0x7a120ee8x14
abilities-probe: after Projectile_Player_Staff_Fireball_1: ... 0x1d4402efx123 0x7a120ee8x14
```
- The two idle programs are zone impacts: `739dc772` in `Zone_OxygenReplenish_AirBubbles(_Savepoint_Altar)`, and
  `ab167681` in `Zone_Ancient_HolyFlame_PushBack` and two other push-back zones. So Impact programs run on the
  dedicated server with no player near.
- Spawning the vanilla templates with kiln.spawn started their programs by FNV(GUID).

## 2. A spell end to end: Fireball I (staff) **[data]**

| Step | Resource | Content |
|---|---|---|
| item | `ItemInfo` ffdf27a5-… `Ammo_T1_Spell_Fire_Ball_lvl10` | category Ammunition, `ammunitionType` Spell, `equipment.slot` Spell, `itemClassAlignment` Mage, `sequences[0]`: trigger Primary → `deff43ba-…`. `damageSetup` {speedMod 3.5, dmgMod 1.08, fire 1.0, wood 5}. `impactValues`: Sint32 `0xaf236ece` = **53 (mana cost)**, Float `0x579b2657` = 2.5, Float `0x41a7d373` = 1.0, Float `0x63d242a0` = 2.6, Material, AttackCategoryMask [Magic, AoE], DamageScaleAttribute. `uiValues` 53 / 2500 ms / 2.6: the tooltip only. `itemTargetingSetup.targetingRange` 42. `equipment.placedEntity` = the projectile template. |
| staff | `Weapon_T1_2H_Staff_01` | slot RangeWeapon, `primaryAnimationSet` Staff, `secondaryAnimationSet` StaffCast, `ammunitionType` Spell, `impactEntity` 7100f7c2-… (shared by staffs and wands), weapon category 15ab545f-…, 5 perks. Spells are the staff's ammunition: the equipped spell item's sequence is what plays. |
| cast | `ActorSequenceResource` deff43ba-…, sub-sequence `Attack_Player_Staff_FixedAnim_Fireball_fast`, 2.083 s | 0.00 AvoidHoles, ShowAmmunition, SetAnimationOverlay, EnableAbility. 0.20 Loop/KeepAlive. 0.40 DisableAbility (evade, block, jump, …) and **SetAnimationEvent** state `bcde8195-…`. 0.63 **VfxNotifier** (hand R). 0.65 SfxNotifier, **SpawnEntityEvent** (RangedCrosshairOrTowardsCombatTarget, template empty: the ammo's `equipment.placedEntity` **[dis]**, see below), **PayUsageCost** (mana), PayUsageCost consume=1 (one charge), CameraShake, TriggerNoise 7.5. 0.70 SetActionOportunity (Evade follow-up). 0.90–0.97 EnableAbility in three steps (the "cooldown"). |
| projectile | `TemplateResource` 65b758f6-… `Projectile_Player_Staff_Fireball_1` | `Impact` → program bae6dc9c-… (id `0x1d4402ef`), `ImpactConfiguration` {Material, AttackCategoryMask [Magic], **Template `0xf6a785cd` = 31e7c423-…** (the explosion)}, Projectile, Gravity + StaticGravityModifier 0.1, LifeTime 5 s, sphere collider, `VfxComponentResource` d623ff26-…, `AudioResourceComponent`, `LerpRenderTransform` from the weapon's head slot, DoNotSave |
| projectile program | `ImpactProgram` bae6dc9c-… (153 words, 28 variables, stream OnCollision) | onEvent(OnCollision) → filterFriends → filterSameRoot → loop forEachCollisionInStream: itemBasedMaterialReaction; if not water: **spawnEntity(SpawnTemplate)**, spawnEntity(SpawnTemplate2), triggerNoise(NoiseRange), **DSELF**; RVM (yield) each tick. The game ships 28 `Explosion_Player_Spell_*` templates (Fireball_1..7 each with an `_on_Water` twin, IceBolt_1..7, AcidCone, Icicle, LightBurst, Meteor, Totem_Fire), so a config swap can change the element of the impact |
| explosion | `TemplateResource` 31e7c423-… `Explosion_Player_Spell_Fireball_1` | `Impact` → 80a2448f-… (`0x7a120ee8`), configs: HitReactionType Medium, WeaponCategory (staff), Material, AttackCategoryMask [Magic, AoE], NoiseRange 25. Combat sphere collider, LifeTime 1 s, VFX b0b78a7d-… |
| explosion program | 80a2448f-… | generic AoE: forEachCollisionInStream → **applyDamage** (25 args; Damage `0x72864524`, AttackMod, HitReaction, PushBackStrength, DamageScaleAttribute, IgnoreConsecutiveHits) → if not blocked: **applyBuff** (DebuffType `0x5adb2644` + DebuffDuration `0x130a0ef2`) and a second applyBuff; optional SpawnTemplate_Pre/Post, WaitTime |

**What an empty SpawnEntityEvent spawns [dis]** (server svn 1287369; `actor_spawn_entity` `0x454f0` → the command builder `0x3f6a0`, the event's
`templateReference` is `+0x80`; the trigger context's `usedItem`/`usedAmmunition` ItemInstanceInfo are `+0x30`/`+0x44`, itemId at `+8` of each):
1. Item of `usedItem.itemId` found: if the event's template is empty, the template is that item's `+0x3c0` = `equipment.placedEntity` (`+0x104 + 0x2bc`). Unless
   `ignoreSpawnVelocity` (event `+0x12f`), the velocity is its `+0x430` = `equipment.placedEntityLocalSpawnVelocity`.
2. Item of `usedAmmunition.itemId` found: the same, and it wins: with the event's template empty, the template is the **ammunition's** `placedEntity`. Its velocity replaces the
   weapon's only when it is not zero. With `ignoreSpawnVelocity`, the velocity is the event's own `spawnVelocity` (`+0x130`).

So a staff cast fires the spell item's `placedEntity` at the spell item's local velocity: `Ammo_T1_Spell_Fire_Ball_lvl10` {0, 0, 30}, Fire Bolt 42. **A spell's speed is on the item, not on its
projectile template.**

**Where a real cast's damage comes from [dis].** The command carries the used items; the spawned entity gets `ImpactItemConfig` (`0x4006f1f8`, 0x2c B: the two ItemInstanceInfo). The spawnEntity
native's helper (`0x18c260`) passes it to what a program spawns, so a projectile's explosion has it too. At instance start, after the template's configs, `impact_initialize` runs
`0x191c10`: both items' `impactValues` go in, then `0x192250` writes the config `Damage`:
`Damage[kind] = (power(weapon) + power(ammo)) × (weapon.distribution[kind] + ammo.distribution[kind])`, wood, stone, metal and explosion unscaled (`0x936040`), when the powers are not both 0.
`power(item) = int(int(genericWeaponSeed × powerCreepWeapon × level + genericWeaponSeed) × dmgMod × speedMod)` (`0x91e9d0`; `keen::BalancingTable` 82706b40-…: seed 10, creep 0.15). Items without
`damageSetup.isSet` count 0. Check: Glacier's dmgMod 1.6 gives 17, 20, 22 at levels 1, 2, 3 and 132, 136 at levels 49, 50, the numbers docs/re/items.md read on real instances.

So in a real cast **a `Damage` config on the explosion template is overwritten**; without used items (kiln's `RunImpact`, a trap) it stands. A staff's distribution is empty, so the spell says
the element and the staff adds power: Fireball I (level 10, dmgMod 1.08, speedMod 3.5) has power 94, a level 5 staff (dmgMod 0.5) 8, so 102 fire before attributes and armour.

The damage number is not in any program. ApplyDamage (`0x17cf80`) combines:
- the config `Damage`, written as above;
- `AttackMod`;
- the attacker's attributes (MagicDamageMod, Intelligence through DamageScaleAttribute);
- the target's armour, susceptibility, block and crit (combat.md §3).

**Tooltips pair with configs.** Each `uiValues` entry belongs to the `impactValues` entry whose `locaTag` hashes to
its `locaId` (FNV of the GUID), with the value scaled by `valueFormat`:
- ManaCost 53 → uiValue 53 (Normal);
- `0x579b2657` 2.5 → 2500 (Duration, ms);
- `0x63d242a0` 2.6 → 260 (Float, ×100).

A tweak can therefore update the matching tooltip number. **[data]**

`impactValues` that no program reads are C++ parameters. Proven for costs: `actor_pay_usage_cost` (0x45360) walks
the current sequence window's `PayUsageCost` events (type hash `0x4e5c0937`). `0x40d00` then reads ItemRegistryFind
(cost item) → `ItemInfo+0x5F8` and the event's own `impactValues` through `0x1ba980(…, configId)`:

| configId | Cost | Attribute | ignoreCostMask bit |
|---|---|---|---|
| `0x727d7f78` | stamina | `0x04B6AA8B` | 1 |
| `0xaf236ece` | mana | `0x6AA2574E` | 2 |
| `0xc8492400` | fuel amount | Fuel `0x05B0BF75` | 4 |
| `0xe99aaff8` | fuel charges | ×charge factor | 8 |
| `0x2de5f1f5` | special ability | `0x046F1FB4` | 0x10 |

`consume` (+0x71) uses up the item. Abilities pass a **cost item**: `Ability_DoubleJump` stamina 10,
`Ability_Blink_Cost` 20, `Ability_SpecialAbility` special 100, `Ability_Skill_Warrior_WhirlAttack` stamina 75.
**[dis] [data]**

The tooltips name two of them: `0x579b2657` is **Charge Time**; `0x63d242a0` is a tooltip-only slot with no reader, whatever its loca tag says (Radius on Fireball I, Debuff Duration and Slow on Ice Bolt I, twice on one item) **[data]**. `0x41a7d373` (1.0, 1.25; no tag) is open. Who reads Charge Time is
probably `server_player_ranged_weapon` [hyp].

**Wand (Frost Wand `Weapon_T5_1H_Wand_01_Ice`)**: slot MidRangeWeapon, animation sets Wand / WandCast.
- Five sequences:
  - Primary `Attack_Player_Wand_IceChain_normal` 1.417 s: PayUsageCost 0.05, SpawnEntity 0.35 and 0.40, TriggerNoise.
  - SpecialAbility: needs skill 9db5d6f3-…, cost item `Ability_SpecialAbility`.
  - Primary with `IsEvading` + a skill (dodge attack).
  - Heavy with `IsAirborne` + a skill (jump attack).
  - Heavy.
- Configs: ice damage, dmgMod 1.0, speedMod 0.666.

So a wand's spell is its own sequence; staffs take the spell from the ammo item. **[data]**

Counts **[data]**:
- 120 spell ammo items, 108 wands and 55 staffs (by `primaryAnimationSet`), 26 `Ability_*` items.
- 2,688 items with sequences (triggers: Primary 3385, Heavy 1089, Interact 652, SpecialAbility 462, Parry 417,
  Block 415, Secondary 101, Evade 4, Jump 3).
- 3,347 items with impactValues, 1,434 with an `impactEntity`, 30 with `equipment.program`.

## 3. The Impact VM

### Format (reflection; kfc-parser's docs agree: code, shutdown code, data) **[refl] [data]**
`keen::ImpactProgram` (64 B, resource type `0xd4922d88`, 571 resources, 1.28 MB, listed by the one
`ImpactRegistryResource` c4933fae-… = 8 + 571×16 B):
- `+0x00 id` ProgramId; `+0x04 programGuid`; `+0x14 u16 stackSize` (256 in the examples);
- `+0x16 usedStreams` (Bitmask8 OnCollision/OnHit/OnParry/EntityInRange);
- `+0x18 code` and `+0x20 codeShutdown` (`BlobArray<u32>`);
- `+0x28 dataLayout` (`BlobArray<ImpactVariable>`); `+0x30 data` (initial values, also the literals: e.g. a "1::value"
  variable); `+0x38 queryCallIds`.

Editor metadata `ImpactInfo` (node GUIDs, `programFlow`, debug info `ImpactFunctionCallDebugInfo{nodeName, nodeId,
pc}`) is not shipped (0 resources).

### Opcodes (enum `keen::ImpactOps`; operands from the interpreter 0x990920, client 0x1239960) **[refl] [dis]**
| # | Op | Operands | Effect |
|---|---|---|---|
| 1–4 | IADD ISUB IMUL IDIV | – | int, pop 2 push 1 |
| 5–7 | ILT IEQ ILEQ | – | compare |
| 8 / 9 / 10 | BR / BRT / BRF | pc | jump; pop and jump if ≠0 / =0 |
| 11 | ICONST | u32 | push (also variable handles `0xFFFF0000 | index`) |
| 12 / 13 | ICONST0 / ICONST1 | – | push 0 (= "input not connected") / 1 |
| 14 / 15 | INC / DEC | – | top ±1 |
| 16 | COPY | src var, dst var | copy `size` bytes between variables of the same type (error 0x2C/0x1F otherwise) |
| 17 | DUP | – | |
| 18 | CALL | target pc, u32 | pushes operand 2, frame, return pc (subroutine) |
| 19 | ECALL | call-site u32, native name hash | pops `argc` handles (≤31), calls the native, pushes its result byte; result codes 1–3 = suspend (the same ECALL runs again next tick) |
| 20 | RET | – | |
| 21 / 23 | LOAD / STORE | frame slot | frame-relative |
| 22 / 24 | GLOAD / GSTORE | var handle | typed by the variable's type hash: 32-bit (`0x32940bec`, `0x9859f31e`, `0xe89f7410`), bool (`0xc894953d`), Time (`0xdfe4e404`, ns ↔ ms); else "Can not laod/gstore Type" |
| 25 | LTIME | – | push ECS time (ms) |
| 26 | TIMEFF | – | float seconds → ms |
| 27 | POP | – | |
| 28 | RVM | – | yield: resume at the next pc next tick |
| 29 | DSELF | – | instance flag 0x40 = `Result_DestroyEntity` |
| 30 | HALT | – | flag 0x08 `Halt` → `ImpactHalted` component (runImpact 0x99c1e0) |

- Instance flags follow `keen::impact::Flags`: Initialized, Executed, CleanupDone, Halt 0x08, Error 0x10,
  TemporaryDisabled 0x20, Result_DestroyEntity 0x40, ShutdownSheduled 0x80. Instance layout as in EMF plumbing §5.3:
  `+0x18` data, `+0x30` stack, `+0x1EC` pc, `+0x1EE` sp, `+0x1F2` flags. **[dis]**
- Variable value address = instance data + `dataLayout[h & 0xFFFF].offsetInBytes`. **[dis]**
- A prototype clean-room disassembler built from this table (scratch: `abil/impdis.py`) decodes **all 571 programs
  with 0 unknown opcodes**; every ECALL hash is one of the registered natives. **[data]**

### Natives **[dis] [rt]**
- **Registration.** `registerNative` (0x996b80, client 0x123fbc0; EMF signature unique on both)
  `(registry, u32 FNV1a(PascalCase node name), fn, Slice<char>* name, u8 argc)`.
  - The registry is `[systems+0x3E8]`, reached from programs as `EcsIterCtx+0x110`. It is a hash map at `+0x20`:
    bits `+0x28`, cap `+0x30`, keys `+0x48`, 0x50-byte values `+0x60` (`+0 fn`, `+8` name slice into an inline
    32-char buffer, `+0x48` argc), count `+0x74`.
  - An existing key is **overwritten** (fn, name, argc).
  - All 108 calls sit in the server world setup 0x233ee0 at sites 0x23650a..0x2381ce. Full table (site, fn, hash,
    argc, name): scratch `out/natives_server.json`.
- **ABI.** `Result* fn(Result* out, Slice<u32>* args, NativeCtx* ctx)`. `out`: u8 error, value byte at +8.
  `ctx`: `+0` dataLayout, `+8` count, `+0x10` instance data, `+0x30` EcsIterCtx. A null fn pushes 1 (the three
  debugDraw natives).
- **Runtime facts (kiln.abilities lab)**: the server world fills its ImpactRegistry at step 2 (empty at the first
  Pre); natives and runImpact ran on the tick thread on the dedicated server; instance layout `+0 impactId, +4
  programId, +8 entity, +0x18 data, +0x20 data size, +0x1ec pc, +0x1ee sp, +0x1f2 flags` (checked against the
  program at run time); the native context `+0x40 impactId, +0x54 programId, +0x58 call-site id`; the native result
  `1..3 = suspend` re-runs the ECALL with the same stack next step (a C# native answered one step later).
- **Runtime:**
  ```
  native registry (EcsIterCtx+0x110) 108de8ad0: count(+0x74) = 107, cap(+0x30) = 256
    native 0x61b11b03 SpawnEntity: fn +0x18bd80 argc 13 name 'spawnEntity'
    native 0x91f30b26 ApplyBuff: fn +0x17cb50 argc 5 name 'applyBuff'
    native 0xb04d0e7a ApplyDamage: fn +0x17cf80 argc 25 name 'applyDamage'
    native 0x044e4187 changeAttributeValue: fn +0x180ee0 argc 6 name 'changeAttributeValue'
  ```
- **Native set (by use across the 571 programs):**
  - most used: applyAttributeValue/revert 397 calls (106 programs), applyBuff 286 (162), getRoot 280, spawnEntity
    277 (168), getTransform 201, applyDamage 190 (161), getSelf 189, mul 143, fetchAttribute 142, onEvent 127,
    forEachCollisionInStream 125, forEachHitEvent 105, hasFaction 100, changeAttributeValue 98, hasBuff 92;
  - also: removeBuff, isEnemy, applyOffset, filter* (friends, same root, faction, consecutive hits, entity tag,
    random), queries (start/fetch/count), terraforming, SetEnemyVariable, IsSkillUnlocked, addSkillLevel,
    triggerKnowledge, SendBaseHitEvent, consumeItem, GetSlotTransform, SetTransform, randomBranch/Value.
  - Full counts: scratch `abil/prog_stats.txt`.
- Selected argc: ApplyDamage 25, forEachHitEvent 25, forEachHitEventInStream 25, createDamageSetup 14, spawnEntity 13,
  fetchOnCollisionEvent 11, forEachCollisionInStream 11, terraforming 9, applyBuff 5, applyAttributeValue 7,
  changeAttributeValue 6.

### Execution **[dis] [emf]**
- `impact_simulation` (0x197cb0, flags 0x1200, a job inside the sim step) → `runImpact` (0x99c1e0): instance lookup
  0x9932f0, `getProgram`, interpreter on `program.code`. It runs **every step for every live instance** (lab: the
  projectile program ran 240 times in its 5 s life).
- Shutdown code, instance destroy and `ImpactHalted` per EMF plumbing §5.2.
- `ecs::Impact`, Health, BuffComponent and the attribute components are server-only (EMF combat §6). Clients never run
  Impact programs; they see the results (entities, hits, replicated attributes, `BuffNetworkData`). [emf]

### Lab limits **[rt]**
Spawned on a Vukah with no player near:
- `Explosion_Player_Spell_Fireball_1`: its program ran 14 times and the entity was gone within 10 steps.
- `Projectile_Player_Staff_Fireball_1`: it sat for its 5 s LifeTime without moving.

Neither changed the Vukah's health (119/119, no HurtEvent). Collision streams get no events where nothing is
simulated: AI, physics and paths need a player near (spawn.md; creatures-research saw the same for AI movement).
Damage from spawned ability templates therefore needs a real client to verify.

## 4. Abilities, skills, perks, buffs

### Active abilities are hidden items **[data]**
- 26 `Ability_*` items (category Materials, no slot) carry `sequences` by trigger: Jump (`Ability_DoubleJump`),
  Evade (`Ability_Evade`: 3 variants), Primary/Heavy (jump and dodge attacks), SpecialAbility, Skill_Warrior_*.
- `ItemSequence` (236 B):

  | Field | Content |
  |---|---|
  | `triggeredBy` | `actor::ActionSequenceTrigger`: Primary, Primary_Tap/Hold, Heavy, Secondary, Tool, Evade, Block, Parry, Jump, Glider, Interact, SpecialAbility, DropItem, Sneak, Sit, Climb, HitReaction, Respawn, **ExternalTrigger**, Parried, ExternalAbortAndOverride, Death, … |
  | `requirements` | `SequenceRequirementMask`: IsEvading, IsAirborne, TargetLockEngaged, … |
  | `requiredSkill` / `forbiddenSkill` | skill-node GUIDs |
  | `costOverride` | the cost item |
  | `knowledgeTrigger` | |

- Examples:
  - The double jump: `Ability_DoubleJump` Jump → `e7b5dbb5-…` (`Ability_Movement_Skill_JumpDoubleSecond`, 2 s:
    SetAnimation, JumpEvent, PayUsageCost 0.017 s, glider allowed at 0.2 s), required skill dc0e7a77-… (FNV
    `0xffd5f940` = an ActiveSkill node, cost 2, `activeSkillUnlockRef` = the `Ability_DoubleJump` item).
  - Blink (`Ability_Evade` with skill 3576302f-…, cost `Ability_Blink_Cost`): `Ability_Movement_Skill_Blink` 0.95 s:
    3 SpawnImpact (programs f2e7b050, d192fd60, 1b8b4e3a), SetGravity, ChangeColliderSetup, camera override.
- **Event vocabulary** (2,157 actor sequences, 2,162 sub-sequences, 89 event types):
  - presentation and control: SfxNotifier 3802, SetAnimation 2718, EnableAbility 2217, VfxNotifier 1993,
    TriggerNoise 996, DisableAbility 980, SetState 667, RotationLerp 661, SetActionOportunity 474, EnemyCommand 412,
    KeepAlive/Loop 398, CameraShake 220, RootMotionWarp 202;
  - gameplay: SpawnImpact 1270, SpawnEntityEvent 992, PayUsageCost 230, ApplyBuffEvent 120, JumpEvent,
    ImpulseEvent, TeleportToTarget, SetGravity, ChangeColliderSetup, CleanseBuff, ChangeVoxel.
- Execution: `actor_spawn_entity` (0x454f0, flags 0x200) switches on the event type hash:
  - `0x51e20bf3` SpawnEntityEvent;
  - `0x64b5dc20` SpawnImpact: spawns `templateReference`, or a default impact template (GUID bytes in the immediates
    `0x47674ea2e97a20a4/0xc14858f41fc4ae89`), with `ecs::Impact`, a collider when `colliderData` is set, and
    `SequenceEventImpactConfig` (`0x0e19190e`);
  - `0xbc11b989` (probably SpawnEntityPerAggroTarget) [hyp for the name].

  `actor_pay_usage_cost` handles PayUsageCost, `player_control_action_sequence` (0xc8c40) player triggers. The same
  code serves players and enemies. **[dis]**
- Player input is a fixed enum `ecs::PlayerInputType` (56 values: Mainhand*, SpecialAbility*, QuickUse0..7, Block,
  Evade, Jump, Glider, RangedCombat*, …) inside the replicated `PlayerInput.fromClient` (`ClientPlayerInputData`, 952 B,
  plus versioned actions such as `unlockSkillAction`, `itemAction`, `triggerEmoteAction`). A vanilla client cannot
  send a new key. **[refl]**
- Server-side start of a sequence: `ActorInput.triggerContext` (`ActionSequenceTriggerContext` 368 B: runtime id,
  trigger type, input type, cost item, used item, ammunition, …). The writer is `0x369d0` (80 call sites: death
  0x89a64, fall damage 0x8a720, player actions 0xc9aaa, …): `(SequenceRuntimeId* out, ActorInput*, {u32 entity, i64
  time}*, u32 sequenceId, u16 trigger, u8 inputType)`; it clears the context (0x170 bytes at +8), writes
  `{sequenceId, time, entity, callIndex = ActorInput.triggerCallCounter (+0x178)}`, `triggerType` (+0x20) and
  `inputType` (+0x22). Fall damage passes trigger 24 (ExternalAbortAndOverride) and inputType 0x38. The actor's
  sequence system consumes the context in the next step (it reads back 0 two steps later). **[dis] [rt]**: kiln.abilities
  `sequence_play` triggered the Vukah swing a837f190 on a spawned Vukah with no player near: `Actor.sequenceRuntimeId`
  became 0x485a4a65 and the swing's SpawnImpacts ran (2 instances of 0c3a7176, root = the Vukah).

### Skill tree **[data] [refl]**
- One `SkillTreeResource` (108ac23f-…, 78 KB): 243 nodes (Root 12, Attribute 65, ActiveSkill 43, PassiveSkill 123;
  Ranger 74, Mage 74, Warrior 64, Neutral 31) and 312 links (`source`, `target`, `isBidirectional`).
- Node fields: `id` (= FNV of the editor node GUID), `uiPosition`, name/description/perLevel LocaTagIds, icons,
  `impactReference` + `configValues` (172 nodes have a program), `uiValues`, in/out links, `costs` (skill points),
  `type`, `classAlignment`, `maxLevel`, `levelIndex`, `activeSkillUnlock` (5 nodes unlock an Ability item).
- Programs:
  - `player_skilltree` (0xd4780, flags 0x210) handles `UnlockSkillAction` input → `UnlockedSkillNodes` (256 known
    ids, `activeSkillImpacts`, levels, version) and `PlayerSkillsUi`;
  - `handle_skill_impacts` (0xc2d40) spawns the nodes' impacts (attribute and passive effects as programs with
    `SkillIdComponent`);
  - the client draws with `skilltree_ui` (client 0x318460, flags 0x201) from its own SkillTreeResource.
- Gates: item sequences check `requiredSkill` against the actor's unlocked nodes; programs call `IsSkillUnlocked`
  (28 calls).

### Expanding the skill tree (skilltree; server svn 1287369 / client 26430625)
Ghidra 12.1.2 decompiles of the current exes; the game's reflection (`tools/kfc types`); the lab `skilltree` (port
26290, mods/tests/skills-test). Usage: docs/kiln/services/skills.md.

**The two components [refl] [dis]:**
- `UnlockedSkillNodes` (2316 B) is a **slot list by id**: `initialized` +0, `knownSkillNodes[256]` +4,
  `activeSkillImpacts[256]` +0x404, `unlockLevel[256]` +0x804 (level = byte >> 5), `version` +0x904, `changeCount`
  +0x908. NodeSet (`0x91fe90`) takes the first free slot. So 256 is the most nodes **one character** can have
  unlocked, not a cap on the tree.
- `PlayerSkillsUi` (136 B) is derived from it by SkillUiRebuild (`0x92d670`):
  - `skillUnlockMasks` = 4 × u64, **one bit per tree index**: the id → index map at SkillTree+0xf8, bit index;
  - `unlockLevel[100]` +0x20, **one byte per levelIndex**;
  - `changeCount` +0x84.
- The rebuild has no bound check. An index ≥ 256 would write into the level bytes, and a levelIndex ≥ 100 into
  `changeCount` and beyond. An id the tree doesn't know makes it return false.
- So the **hard caps are 256 nodes in the tree and 100 level slots**. The vanilla tree uses 243 nodes and levelIndex
  0..90: 13 native slots are free, 9 of them for nodes with levels.

**The tree singleton [dis] [rt]:**
- Layout of `g38_skilltree::SkillTree` (0x1a0 B, created in world setup `0x234903` = `0x926890(alloc, resource
  system, &config+0x1a0 GUID)`):

  | Offset | Field |
  |---|---|
  | +0x08 | resource system |
  | +0x10 | handle |
  | +0x20 | SkillTreeResource data |
  | +0x2c | resource version (`unlocked.version` is compared with it) |
  | +0x38 | GUID |
  | +0x48 | HashMap node id → node* |
  | +0xa0 | link id → link* |
  | +0xf8 | node id → index |
  | +0x150 | id list by index (reserved 256, grows) |
  | +0x178 | roots |

- The loader `0x96ea80` [`0xec6be0`] runs from the per-tick registry update (`0x23b0a0`, before the ECS step) while
  +0x20 is 0. It gets the data with ResourceData, reserves each map for the resource's counts, and inserts every node
  and link.
- Kiln serves the expanded resource (kiln.assets override) right before the loader runs, and the game builds
  everything from it. **[rt]** "skill tree loaded 261 nodes and 330 links ...: node map 261 of 1024, link map 330 of
  1024, index map 261 of 1024, id list 261 of 512": the game sizes its own maps; only the masks cap the tree.
- Every node of the 312 game links is in its source's `outgoingLinks` and its target's `incomingLinks`. Reachable
  (`0x91f990`) walks both lists: a node is unlockable when the source of an incoming link is unlocked or a root, or the
  target of an outgoing link is unlocked. **[data] [dis]**

**Joins, saves, unknown ids [dis] [rt]:**
- The character save keeps `count, (u32 id, u8 level)*` (the save editor's `player_character` block). `initialized`
  is not saved, so `player_skilltree` runs the join check (`0xe00b0`) for every joining player:
  1. SkillUiRebuild;
  2. connectivity (`0x96f8b0`: BFS from the roots over native lists);
  3. levels (`0x96f900`);
  4. spent ≤ total (unless the free-skills flag `0x9398f0` is set).
- Any failure → the free reset `0xb9a00(iter, 0)`: every id without levels removed, every level 0.
- So **a node id of a mod that is gone resets the player's whole tree** on the next join. **[rt]** simulate: "AddRaw
  0xdeadbeef: ... rebuild False"; after Kiln's strip "rebuild True".
- The check waits (result 1) until the player's skill-point knowledge is there (PointsTotal `0xaa810`): a
  player-template entity (`1_Player`) never gets past it **[rt]** ("UnlockedSkillNodes initialized 0" for ever).
- **A forced tree can fail it [data] [rt]:** the 21 nodes of a forced relearn left out `0xffd5f940`; `0x4475e8dd`'s other neighbours aren't in the
  list, so step 2 fails and each join resets the tree. With `0xffd5f940` in, the same list passes (skills-test, the game's own check).
- Unlearning one node (`0xbbab0`) charges its cost in respec items (`0xb9230(iter, cost)`) and undoes the NodeSet
  without them. A paid respec charges the points spent.

**The client's screen [dis]:**
- `skilltree_ui` (client `0x318540`, flags 0x201; reads PlayerSkillsUi and its own SkillTree) walks the tree's id list,
  and keeps one 0x44-byte record per node in G38UiPlayData (HashMap at +0x449198: level `+0x41`, low bits `+0x42`,
  unlocked `+0x43`).
- It sets "unlocked" from the mask bits, bounded by both 256 and the id-list count. It copies the masks to +0x44b218
  and computes the points shown with the client's PointsSpent (`0xe7b170`).
- So the screen draws whatever the client's own tree holds. A vanilla client with 243 nodes ignores bits past 243,
  but its PointsSpent would read past its node array.

**Who reads the masks (the shadow table's census) [dis]:**

| Function | Call sites (server / client) | Kiln |
|---|---|---|
| IsUnlocked `0x94c480` [`0xea5230`] (id → index → bit) | 50 in 23 functions / 53 in 25 (item sequences' `requiredSkill` `0x53b30`/`0x53c70` ×7 each, the natives, buffs, durability, mining, camera, progress scaling, ...) | hook |
| NodeSet `0x91fe90` [`0xe78d20`] | 3 (+ kiln.progression) | hook |
| Reachable `0x91f990` [`0xe78960`] | 1 | hook |
| PointsSpent `0x9222f0` [`0xe7b170`] | 3 / 4 | hook |
| Reset `0xb9a00` [`0x2ce370`] | 2 | hook |
| Join check `0xe00b0` [`0x2f4110`] | 1 | hook (login guard) |
| skilltree_ui [`0x318540`] | client | post-hook |
| SkillUiRebuild, the level check `0x96f900` | 3, 1 | none: they only see UnlockedSkillNodes |
| connectivity BFS `0x9160a0` (in `0x92e000`) | 2 | none: shadow links stay out of native nodes' lists |
| per-class sums `0x922690`, camera SkillModifier `0x91afd0` | 1, 1 | none (inline; skip shadow nodes) |

- Every query that matters goes through one helper (IsUnlocked). The only inline mask readers are the connectivity
  walk (avoided by the link rule), the camera modifier and the per-class sums.
- A skill impact (`0xb69a0`) is an entity with: SpawnTime, `SkillIdComponent`, `ImpactHierarchy` (the player),
  `Impact`, `DespawnLink` (the player), `DoNotSave`, and for nodes with levels `Level {storage = {level, 99}}`. Kiln
  spawns its shadow nodes' impacts the same way. **[refl] [dis]**
- **[rt]** the shadow path through the game's own input handler (`PlayerInput.fromClient.unlockSkillAction` +0x180,
  `ServerConsumedPlayerInput.consumedUnlockSkillAction` +0x58):
  - "the game's unlock handler put the shadow node in Kiln's table (level 1), not into UnlockedSkillNodes";
  - "IsUnlocked(skilltest_deep_a) is true now, and skilltest_deep_b (linked from it) is reachable";
  - "the game's PointsSpent adds the shadow table: 0 -> 6";
  - "the game's respec ... clears the shadow table too".

**Why not grow the components:**
- PlayerSkillsUi and UnlockedSkillNodes are fixed-size reflected components, replicated (the network serializer
  works from the type's layout) and saved (UnlockedSkillNodes in the character file).
- Growing them would mean changing the ECS type registry's size, every archetype chunk holding them, the replication
  and save serializers, and every reader that hard-codes 256 / 0x20 / 0x804 (the census above). It would also make
  every character saved on such a server unreadable for vanilla servers and clients.
- The shadow table changes no layout and no save. Its cost: 7 hooks on the client, 6 on the server.

**In a client [rt]** (client 26430625, a hosted local game):
- The client's own SKILLS screen draws Kiln and shadow nodes from the served resource, and its input works for them.
  kiln.log: "the new world's skill tree loaded 263 nodes and 332 links (expected 263 / 332, Kiln's resource)", once for
  the hosted server world and once for the client world. Space unlocks a shadow node: "skills: shadow node 0x7cb432ed
  of entity 1: level 0 -> 1 (the game's skill tree)".
- The game's UI has no free respec: unlearning and respec cost respec items, also for Kiln nodes.
- The session found two gaps in Kiln.Skills, not in the game. Both are fixed and lab-tested, not yet client-tested:
  - a hosted game's request for a data-only Kiln tree had no scheduler to run on, so Ember Arts reported "the server
    does not have Kiln talent trees";
  - kiln.players reports the host of a local game with Steam id 0, so its shadow table and talent state lasted only the
    session. The save key is now the Steam id, else the account id, else 1 for the host.
- Details and the manual test: docs/kiln/services/skills.md, "What a client shows".

**Attribute references [rt]:**
- The game's attribute nodes use one program (`937e39f4`) with an AttributeReference config (`0x36f0ee47`) and an
  amount (`0x0a0c87e7`).
- The GUIDs, matched in the lab through each attribute component of a player entity:

  | Attribute | GUID |
  |---|---|
  | Strength | `64810163-…` |
  | Constitution | `0b1ceec5-…` |
  | Intelligence | `2609852d-…` |
  | Spirit | `0e31beac-…` |
  | Dexterity | `4be83310-…` |
  | Endurance | `6bd1a147-…` |

### Perks **[data]**
365 `Perk` resources, listed by `PerkCollectionResource`. Two kinds:
- simple attribute buffs with `damageModifier`/`perkArmorSetup` and no program, e.g. `Perk_Damage_Ice`;
- programmed perks, e.g. `Perk_OnKill_Restore_Durability` → b000cad2-…: forEachHitEvent → getRoot/isSameEntity →
  wasKillingBlow → fetchAttribute(DurabilityMax) → add → set.

Items carry 5 `perkReferences` + `perkIds`. `equipment_impacts` (0x147330) creates perk and gem impacts from
`PerkContainerNew`; `apply_armor` and `rebalance_perks_and_level` read the PerkRegistry. **[dis] [rt]** per item in an equipment slot
(`0x145570`): for i < `unlockedPerks`, a perk with a program gets an impact entity (`equipmentImpacts[slot].perkEntities[i]`); locked ones get
none, and a new item has none unlocked, whatever its rarity. Leaving the slot destroys them; a hotbar switch does (slot 0 follows the selection).
Lab `perkhit-test`: "perk entities [0,2601,2602,0,0]", "another hotbar slot: the perk's program is gone", "2602 -> 2617".

The one on-hit program (`usedStreams` OnHit), `4abf7534-…` (all seven `Perk_OnDamage_Dealt_Debuff_Poisoned_T*`): for each hit: same root?
`hasWeaponCategoryType([Dagger])`, enemy faction, `ChanceSkill` 0.3 with the skill `f21866ab-…` else `ChanceValue`, applyBuff(`BuffType`). The weapon check is why
it does nothing on a sword. `applyDamage` with an empty AttackCategoryMask returns without a hit. **[dis] [rt]**

### Buffs **[data] [emf]**
- 274 BuffTypes in `BuffTypeCollectionResource` 02022e86-… (`buffMap` hashed index + `buffs`): Buff 127, Debuff 118,
  Food 17, TerrainDebuff 11, Rested 1; 223 with a program, 45 saved.
- A buff is its own entity with `BuffComponent` (60 B) under the target's `BuffContainer` (4 food + 16 + 16 + rested +
  terrain slots). It is applied by the ApplyBuff native, `actor::ApplyBuffEvent` (sequence event, can override the buff
  and pass impactValues/damage) or `applyBuffCore` (0x15e130, client 0x3b3460).
- `BuffNetworkData` {lifetime, end, buffTypeId, level} replicates for the UI.
- `getBuffType` (0x933710, client 0xe8c4e0).

### Attributes
Health, Mana, Stamina, SpecialAbility and the ~50 modifier attributes are AttributeComponents (docs/re/combat.md).
Abilities change them through `applyAttributeValue`/`changeAttributeValue`/`setAttribute`. Clients see
`NetworkPlayerAttributes` (256 B: armor, base attributes, movement speeds, crit, damage mods, susceptibilities),
`NetworkHealth`, `NetworkDurability`. **[refl]**

## 5. Animations **[data] [refl]**

| Resource | Count (client = server) | Content |
|---|---|---|
| `JointAnimation` | 5,461 / 5,461 (169 MB) | `hierarchyHash`, `positionFactor`, `positionTangent`, `scaleFactor`, `headers` (segments of ~63 frames: frame range, used/non-constant orientation, position and scale joints), `data` (16-byte `vf32_data` blocks: quantized SIMD), `fps` (60), `frameCount`, `jointCount`. Example dfce565d-…: 399 frames, 102 joints, 7 segments, 5,846 blocks |
| `RootMotionAnimation`, `LveAnimation` | 578 / 578 each | root motion (float4/float3 keys); LVE tracks |
| `anim_graph::runtime_graph::AnimationGraphResource2_0` | 279 / 278 | parameters (float/uint/int/bool/id), `hierarchy`, 36 slot bones, `nodeDefinitions` (variants: StateMachine, StateMachineState, Transition, SampleAnimationClip, BlendSpace_1D, LayerBlend, SelectPoseBy*, IK solvers, Retarget solver, …), IK chains, sync nodes, feet IK |
| `ModelHierarchyResource` | 257 / 246 | skeleton: joint name hashes, parent indices, skin matrices, default pose (player: 87b29df6-…, 102 joints) |
| `anim_graph::RetargetDataResource` | 3 | bone scale/elongation factors |
| `AnimationSequenceContainer` + `SequenceResource` | 2,896 + 2,901 | notifies inside clips: SfxNotifier 7565, FoleyEffect 1344, EnvironmentEffect 1324, foot planting 1190, cloth 533, VfxNotifier 343, TriggerNoise 131. **Only presentation and noise** |

- **The player graph** 21319e83-… has 2,944 nodes, among them 1,064 SampleAnimationClip, 396 states, 72 transitions,
  293 + 118 IdParameterEquals conditions and 107 SelectPoseByParameterId.
- **How a sequence picks an animation.** `actor::SetAnimationEvent.animationName` is a StateId GUID. Its FNV
  (`0xa34996b4` for the Fireball cast) is the value the graph's id parameter `1563163402` is compared against
  (IdParameterEqualsConstant / …List nodes in the player graph; found only there).
- **State replication.** The server writes `ServerAnimationGraphInput` (state hash, inputs, overlays);
  `server_to_network_animation_graph_input` copies it into the replicated `NetworkAnimationGraphInput`; the client turns
  it into `ClientAnimationGraphInput`. The client animates from **state ids and inputs**, not clips. **[refl]**
  (program names/flags: server 0x1099c0, client 0x3415d0 flag 0x1)
- **Clip nodes.** `SampleAnimationClipNodeDefinition` (212 B):

  | Field | Meaning |
  |---|---|
  | `animation` | JointAnimation reference |
  | `rootMotionAnimation` | root motion |
  | gender alternatives | |
  | `animationDuration` | |
  | `loop` | |
  | `speedController` | raw `speedMultiplier` (1.0 in all 1,064 player clips) or an input-driven factor |
  | `hasSequences` / `sendEvents` | the clip's notifies |

- **Weapon sets.** `ItemInfo.equipment.primaryAnimationSet` (None, SingleAndShield, SingleNoShield, DualWield,
  TwoHanded, Bow, Staff, Wand, GreatSword) and `secondaryAnimationSet` (None, Bow, StaffCast, WandCast) go into the
  `EquipmentAnimationSets` component (2 B), which graph inputs read (`actor_trigger_jump_land_animation` reads it). The
  attacks themselves come from the item's sequences.
- **What the server needs.** Hit timing is **sequence time** (SpawnImpact/SpawnEntity events at fixed times), not an
  animation notify. The server still evaluates the graph (`server_update_animation_graph_2`, root motion,
  `RootMotionWarpEvent`, slot transforms for SpawnImpact `attachmentSlot` colliders and `GetSlotTransform`). So a
  changed clip changes the server's root motion and melee hitbox positions. [refl] [hyp for the slot dependence]

## 6. Server, clients, networking

| Thing | Server | Vanilla client | Tag |
|---|---|---|---|
| resources (programs, sequences, templates, items, perks, buffs, skill tree, arsenals, animation clips and graphs) | all, byte-identical to the client | its own copy | [data] |
| VFX, SFX, render models, UI textures | none (0 VfxResource / SoundResource / RenderModel / UiTexture) | yes | [data] |
| Impact programs, hits, buffs, costs, attributes | run here (server-only components) | results only | [emf] [dis] |
| entities spawned by abilities (projectiles, AoE) | created | created by **template GUID**; drawn from its own template (VFX, model) | [hyp, strong: the spawn path replicates templates] |
| animation | graph evaluated (root motion, slots) | plays the replicated **state id** with its own graph and clips | [refl] |
| sequences | timed gameplay events | `NetworkActor` (`sequenceRuntimeId`, `usedItemId`, `triggerType`, masks) replicates; `actor_client_play` (client 0x2652d0, flag 0x1) plays the client's own sequence copy for notifiers (VFX/SFX/camera) | [refl] [hyp for "own copy"] |
| tooltips | – | `ItemInfo.uiValues`, `uiBuffValues`, `Perk.uiValues`, skill `uiValues`: display-only and client-side; paired with configs by `locaId` = FNV(`locaTag`) | [refl] [data] |
| prediction | authority | a prediction world (kind 1, 80–90 Hz) exists; which actor/cost programs it runs for the local player is **unknown** | [hyp] |

Consequences:
- A server-side number change (damage, mana cost, durations, radius, which existing template a config spawns, buff,
  ability cost) is correct for vanilla clients. They only show stale tooltips and possibly predict with vanilla costs.
- A server-side **timing** change in a sequence makes the server act at the new time while vanilla clients play VFX and
  animation at the old one.
- New GUIDs (templates, sequences, buffs, programs) work on the server. A vanilla client cannot draw an unknown template
  or buff icon: it needs Kiln with the definitions, the same rule as new items in docs/kiln/services/content.md.

## 7. Live editing: where the bytes are and when a write applies
- Resources are resident and read-only:
  - item, recipe and loot data sit in a ~450 MB `PAGE_READONLY MEM_PRIVATE` allocation (content.md) **[rt]**;
  - the arsenal registry points into its resource data in a ~160 MB read-only region **[peer-rt]**;
  - template components are one shared read-only copy per template; a VirtualProtect write "showed up at once in
    existing and newly spawned Vukahs" **[peer-rt]**.
- kiln.assets finds any resource's data by key (`game_handle`/`game_data` inside native/services/assets), so the
  address of any resource is reachable. No public "address of" call exists yet.
- When a write takes effect:
  | Data | Takes effect | Tag |
  |---|---|---|
  | config values in items, templates, sequence events, buffs, skills | next instance (impact_initialize copies at start) | [dis] |
  | costs (`PayUsageCost` + cost item's impactValues) | each payment | [dis] |
  | template components | live: spawn and shared reads | [peer-rt] |
  | sequence events | next playback | [hyp: read from the resource while walking the window, `0x34520`] |
  | program `code` | every run, including running instances (dangerous) | [dis: runImpact fetches the program each call] |
  | program `data` | new instances only | [dis] |
  | animation graph data | unknown: may be baked into per-entity graph instances | [hyp] |
- Size changes (more events, more config entries, more buffs) need BlobArray re-pointing (kiln.content's recipe-list
  technique). New entries in indexed collections need the index updated as well: ImpactRegistry map, BuffTypeMap,
  skill ids in `UnlockedSkillNodes`.

## 8. Open questions
1. Which programs the client's prediction world runs for the local player (costs, sequences, ranged weapon), i.e. what
   a vanilla client predicts when the server's cost or timing differs (real client).
2. ~~The writer of `ActorInput.triggerContext`~~ answered (§4): `0x369d0`; kiln.abilities `PlaySequence`. ~~Why a
   server-started sequence stops on a player~~ answered (§9): keep-alive gates, not prediction; the player's own
   vanilla client shows them (session). Open: the used item / ammunition in the trigger context for real casts.
3. Meaning of item configs `0x41a7d373`, `0x4e133b42`, `0xbc34a504`, `0x1e8302eb` (not program variables: C++ readers in the ranged-weapon programs?). `0x579b2657` and `0x63d242a0`: §2.
4. The JointAnimation codec (`vf32_data` quantization, segment layout), needed for new clips (L4).
5. Animation graph instances: are clip references and speed multipliers read live from the resource?
6. How the client resolves a spawned entity's visuals (template GUID through the replication of creation;
   docs/re/network.md has the channels only) and what it does with an unknown template GUID.
7. Whether `ImpactConfiguration` on a spawned entity is copied from the template or read through the template (both
   would give "next spawn").
8. Collision events for Kiln-spawned impacts near a player (the lab has none): does a Kiln-spawned
   `Explosion_Player_Spell_Fireball_1` damage enemies when a player is near? Does `ImpactHierarchy` (root = player)
   give the hit to the player (XP, faction filters)?

## 9. Sequences on player characters (server svn 1287369, lab `ai-anim` port 26210)

Session #7 saw `PlaySequence` set a player's `Actor.sequenceRuntimeId` to the Fireball cast for about 10 steps, then
nothing (no animation, no mana). The cause is the sequence, not the player:

- **The game's own path for a server-started sequence on a player is the one Kiln uses [dis].** Emotes:
  `player_control_not_predicted` (#43, `0xcfa60`) compares `PlayerInput.fromClient.triggerEmoteAction` with
  `ServerConsumedPlayerInput.consumedTriggerEmoteAction`, looks the emote up in the `EmoteRegistry` (`EmoteResource`
  `ba29e2d5-…`: 11 `{emoteId, chatMessage, actionSequence}`, e.g. `Ability_Player_Emote_Greet` 13fd798d-…) and calls
  the TriggerSequence helper `0x369d0(…, sequence, trigger 22 ExternalTrigger, inputType 0x38)`: the same call, trigger
  and input type as `PlaySequence(…, abortCurrent: false)`. Emotes are not predicted ("not_predicted"): every client,
  the emoting player's own included, plays them from the replicated `NetworkActor`.
- **Keep-alive gates [dis].** The event walker (`0x34980`) handles `KeepAliveEvent` (`0xc6b36da5`) by storing its
  `keepAliveMask`/`abortMask` (`+0x60`/`+0x68` in the event) in the playback context (`sequenceKeepAliveMask`
  `Actor+0xbe8`, `sequenceAbortMask +0xbe0`); `0x36620` checks them **every step** from then on: the sequence ends
  unless the effective state `(Actor.currentState +0xbf8 | playback.setStateMask +0xbd0) & ~unsetStateMask +0xbd8`
  (`0x3b770`) has every bit of the mask. A later KeepAliveEvent replaces the requirement (an empty mask clears it).
  `LoopEvent` (`0xb52eef3f`, `+0x60` mask, `+0x70 jumpToLoopEndOnAbort`) leaves its loop when the mask drops and
  repeats it while the mask is held at its end. For players the masks are input states set by the ranged weapon
  program: `InRangeCombat` 25, `InRangeCombatPreparing` 26, `InRangeCombatShooting` 27 (`keen::actor::StateFlag`).
  The Fireball cast `deff43ba` has KeepAlive InRangeCombat at 0.2 s and InRangeCombatShooting at 0.4 s, loops
  [0.2, 0.4) Preparing and [0.533, 1.966) Shooting, and its animation event at 0.4 s: without held input it ends at
  0.2 s, before its animation and its cost (0.65 s). Session #7's result exactly.
- **Survey [data]:** 257 player sub-sequences; 87 have KeepAlive events, 106 Loop events. Gated: staff casts (57),
  bows and ranged specials, fishing, run-start turns. Not gated: emotes (10), melee (1H 12, 2H 32, dual 10, melee
  specials 45), wand attacks (29: loops only, skipped without input), unarmed, torch, consumables, hit reactions.
- **Lab, a player-template entity [rt].** Spawning `1_Player` (c3db66d6-…) gives a server-side player character
  (`Actor 3832 ActorInput 456 PlayerInput 1472 NetworkActor 208 PlayerState 96`) whose actor/sequence programs run
  like a joined player's (no client behind it; it does not stream terrain). abilities-test:
  ```
  PASS the Greet emote plays on the player character for 92 steps (3.83 s) ... (NetworkActor.triggerType 22)
  PASS a melee attack (Attack_Player_Unarmed_Fists_LightChain01, 1.68 s) plays on the player for 41 steps (trigger 24)
  INFO Fireball cast: 5 steps without the hold, 46 with it (replicated states seen 0x40000e000000)
  PASS a bow shot: 4 steps without the hold, 18 with it (0.8 s = 20)
  ```
- **Kiln's hold** (`native/services/abilities`, "held sequences"): with `SequenceOptions.HoldInputs` (default) Kiln
  parses the sequence's KeepAlive/Loop events (resource bytes through kiln.assets), ORs the needed bits into the
  running sequence's own `setStateMask` a little ahead of each gate, lets a loop's own bits go just before its end (it
  plays once; when the loop needs nothing beyond the sequence's requirement, that release ends the sequence, as a
  player's release would), and takes back only the bits it added. The bits replicate (`NetworkActor.setStateMask`
  `+0x28` carried `0xe000000` during the cast). A held cast spends what the sequence spends (PayUsageCost reads the
  used/cost item: the lab entity has no staff, so nothing).
- **In a client [rt]** (vanilla client joined the lab, the player's own character): emotes started this way animate
  on the player's own screen, a melee special (greatsword whirl) swings with the game's hit markers, the held staff cast
  shows its pose and the sequence's ammunition HUD (client-side events of the replicated sequence). Mana stayed 255: the
  trigger context's `usedItem`/`usedAmmunition` (+0x30/+0x44) are empty, so PayUsageCost and the ammo spawn have no item
  (open: fill them from the actor's equipment).

## Facts for programs Kiln generates **[dis]** (server build 26430253, svn 1287369)
- **A used item's damage.** `0x192250` writes the item's power into a program's first variable of type `impact.DamageSetup`. The scan at `0x98c9d0` compares the type (`0x4af58ef7`) and never the config id. So any program run with `ImpactItemConfig` has its first damage overwritten when the used items' power is not 0, bound to a config or not. Kiln.Blocks keeps that variable for the main hit, or for nothing.
- **Buff references are hashes.** `applyBuff`, `removeBuff` and `hasBuff` run FNV-1a over the 16 bytes (`0x40720`) and go on with the id; nothing else reads the GUID. `BuffType` records hold only the id, and only the references found in items, perks and sequences give a GUID, for a few of the 274 buffs. So any 16 bytes with the right hash refer to a buff (`BlockIds.WithFnv`). Lab: frost slow by its id is applied and removed this way.
- **Arguments read with no check.** A native reads `args[i]` through a pointer it leaves null when the argument is unconnected. Most test a flag first and return an error; some do not, and the process dies. `natives.json` marks those `"unchecked"` (74 arguments of 42 natives, found by a scan of the decompiles and kept only where all 571 vanilla programs connect the argument). `hasAttackCategory.matchAll` (`0x1885d0`) took a lab down.
- **Consuming with no used item.** The spawn path `0x42390` reads `ItemRegistryFind(id)+0x42c` with no check (`0x42586`), so a `consume` event with no used item takes the server down. The cost payer `0x40d00` returns when there is no item, so `PayUsageCost.consume` is safe: 75 vanilla staff events have it on. Events with `consume` on, of 2,157 sequences: SpawnEntityEvent 79, PayUsageCost 75, SpawnImpact 15, ApplyBuffEvent 11, PlaceVoxelObjectEvent 1.
- **Item clones share lists.** A clone made by copying `ItemInfo` and rebasing its offsets still points at the base's lists, so a change to one of their elements changes the base and every other clone. kiln.content now copies every list per clone.
