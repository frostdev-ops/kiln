# Game data at run time: templates, arsenals, sequences, buffs, perks, skills, clones (evidence for kiln.gamedata)

Perk and buff clones (section 5, "Perks and buffs") were done on svn 1287369 (server 26430253, client 26430625).

Build: dedicated server svn 1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`, image base 0x140000000, RVAs below),
client Steam 26404966 (`enshrouded.exe` sha256 `ce6fdba6…`; RVAs in brackets). Sources: the game's own reflection
(TypeByHash at run time; offline `tools/kfc` over the client exe), decoded resources (`tools/kfc decode`), capstone and
Ghidra 12.1.2 over read-only copies, the research in
`docs/re/abilities.md` and `docs/re/creatures.md`, and the lab `gamedata` (port 26100, a copied world, no
player; `mods/tests/gamedata-test`). Nothing from kfc-parser. Tags: **[refl]** reflection, **[data]** decoded
resources, **[dis]** disassembly / decompile, **[rt]** lab runtime (kiln.log quoted), **[hyp]** hypothesis.

## Short answers
- **Every number is reachable without an entity.** The ability and creature data are resident resources: a
  TemplateResource, an ActorSequenceResource, the one EnemyArsenalRegistryResource, BuffTypeCollectionResource,
  SkillTreeResource, the Perk resources. kiln.assets' `resource_list` / `resource_data` (or the resource service's
  slots) give their live bytes; the game's reflection walks them (struct bases, BlobArrays, BlobVariants, optionals).
  **[rt]** "gamedata ready (server world, through kiln.assets) in 17 ms: 12269 templates, 201 arsenals, 2162
  sub-sequences, 274 buffs, 365 perks, 243 skill nodes, 4213 items".
- **Template data is the resource blob itself .** `GetTemplateComponent(world, Enemy01BehaviorState,
  entity)` of a spawned Vukah returns the address of that component inside the Vukah's TemplateResource blob, so a
  write there is what the AI reads. **[rt]** "GetTemplateComponent(Enemy01BehaviorState) = 0x61E633B8 = the template
  data Kiln changed (0x61E633B8); the AI reads viewRange 30". The research's Ghidra pass agrees: TemplateInfo span
  entries are `{u16 typeIndex, void* data}` with `data = variant + 4 + offset` (pointers into the blob, no copy)
  **[dis]**.
- **Read-only private memory**, the same allocation as items: "template data at 000000005FD6D210: protect 0x2,
  MEM_PRIVATE, allocation 000000005CE50000" **[rt]**; kiln.content's writer handles it ("22 to read-only resource pages
  (18 VirtualProtect pairs)").
- **Dynamic components take their template value at spawn.** A Fireball projectile spawned after `LifeTime.lifeTime`
  was set to 8 s had its own (dynamic) LifeTime of 8 s **[rt]**; a clone spawned with `Level.dataStorage[0]` 20 and
  `ScaleComponent.uniformScale` 1.4 had those in its own components **[rt]**.
- **Sequence events must stay sorted** (the walker stops at the first event at or after the window's end) **[dis]**.
- **New templates can be registered at run time** with the game's own per-template call **[dis] [rt]**.

## 1. Reading the data
### Resources
- kiln.assets v1 `resource_data(key, &ptr, &len)` (zero-copy; the game's resident copy unless a mod overrides it) and
  `resource_list(type)`; without it, the resource service `S = *(g38_items::ItemRegistry + 8)`: the ItemRegistry
  constructor stores S at `+8` (`mov [rax+8], rsi` at 0x925747) and calls the Find wrapper 0x829cd0 (`mov rax,
  [rdx+0x7d8]; call rax`) with it **[dis]**. Slots `+0x7d0` by type, `+0x7d8` key → handle, `+0x7e8` handle → data
  (feature `assets.resources`, docs/re/resources.md). Kiln checks `*(S + svc.by_key) == assets/ResourceByKey`.
- Type hashes = FNV-1a-32 of the qualified name: TemplateResource 0x39768775, actor::ActorSequenceResource 0x9fd4a561,
  enemy::EnemyArsenalRegistryResource 0xf67d6fc3 (the `Dev…` variant 0x74b3e434 has the same GUID; the game uses the
  plain one), BuffTypeCollectionResource 0x1be67cef, SkillTreeResource 0xe4b6994f, Perk 0x6084515a, ImpactProgram
  0xd4922d88 **[refl] [data]**.

### Reflection (record layout: docs/re/content.md §2)
- A struct record's `+0x38` is its **base** (`keen::impact::FloatImpactConfig` → `ImpactConfig`,
  `actor::ActorSubSequenceResource` → `SequenceResource`, `ecs::Faction` → `ecs::Component`); its field list holds only
  its own fields, so the walker prepends the base chain **[refl]** (kiln.content's flattener did not need it: ItemInfo
  has no base).
- Blob kinds, offsets relative to the field: BlobArray `{i32 rel, u32 count}` (0x18), BlobString (0x19), BlobOptional
  `{i32 rel}` (0x1A), BlobVariant `{u32 type hash, i32 rel from +4, u32 size}` (0x1B), ObjectReference (0x1C, 16 B),
  Guid (0x1D) **[refl] [data]**. A template is `{name BlobString, family, …, components BlobArray<BlobVariant<ecs::Component>>}`.
- One-field structs (`HashKey32 {value}`, `ProgramId`) are shown as their scalar; `keen::Time` (8 B, ns) in seconds.

## 2. Config values, aliases, tooltips
- `impact::Configurations {simple, scaled}` = two `BlobArray<BlobVariant<impact::ImpactConfig>>`. Every config derives
  from `ImpactConfig` (76 B: `locaTag` 16 B, `isSigned`, `valueFormat`, `id`, `configId` +0x24, `type` +0x28,
  `configGuid`, `objectId`) and adds `value` (+0x4c; scaled ones `scaleFactor`, `function`, `source`, `value` +0x68)
  **[refl]**.
- Aliases: config-bound `ImpactVariable`s (24 B: `name`, `configId` +4, `type`, `size`, `offsetInBytes`, `dbgName`
  BlobString +0x10) of the 571 programs, `dbgName` up to `::` ("PushBackStrength::value"), generic `impact_nodes.*`
  names skipped, plus the C++-read costs. **[rt]** "config vocabulary: 289 config ids with names (459 aliases) from 571
  impact programs and 6 C++-read ids".
- Tooltips: `ImpactUiValue` (40 B: `locaId`, `valueFormat`, `value` s32, `scaleData`, `isSigned`, `isScaled`) pairs
  with the config whose `locaTag` hashes (FNV-1a over the 16 bytes) to `locaId`, value scaled by
  `ImpcatValueNumberFormat` (Normal 0, Percentage 1, Duration 2, DurationMilliseconds 3, Float 4, Numberless 5): Fireball
  I ManaCost 53 → 53, `0x579b2657` 2.5 (Duration) → 2500, `0x63d242a0` 2.6 (Float) → 260 **[data]**. **[rt]** "the
  tooltip value followed (uiValues[0].value 30, original 53); the duration one stayed 2500".
- Program references: `BuffType.impact` etc. are `ImpactProgramReference` = `ObjectReference<keen::ImpactDefinition>`;
  the GUID is the ImpactProgram resource's (`programGuid`), so Kiln checks it against ImpactProgram resources **[refl]
  [rt]** ("a buff's program reference (buff_cdbe9c80 impact 91715e7c-…) takes a loaded ImpactProgram GUID").
  New programs' config ids are FNV-1a-32 of their config name (tools/impact, docs/kiln/modding/impact.md).
- Adding a config: `PushBackStrength` (`0xe5254945`, a `PushBackForceConfig`, enum None/Small/Normal/Big/VeryBig/
  VerySmall/PullSmall/PullNormal) is not in `Explosion_Player_Spell_Fireball_1`'s configs; its program 80a2448f reads
  the variable from `data` (default byte at +704 = 1, Small) **[data]**. An exemplar (the first entry of that id in
  items, perks, buffs, skills, sequence events, templates; "config exemplars: 285 config ids found in the game data (56
  ms)" **[rt]**) gives the variant type and bytes; Kiln re-points the `simple` header at a copy with the new element
  (loca tag cleared). **[rt]** "PushBackStrength Big (added: the template had no such config)"; unload: "(absent)".
- impact_initialize (0x197250) copies configs into each new instance by configId **[dis]** (docs/re/abilities.md §1),
  so a config change applies to the next instance. The instance-side read-back (variable values of a running program)
  needs a hook on the binding (0x98c7f0) and stays a client check.

## 3. Sequences
- `ActorSequenceResource {resourceId Guid, subSequences BlobArray<ActorSubSequenceResource 72 B>}`; a sub-sequence is
  `SequenceResource {events SequenceEvents = BlobArray<BlobVariant<SequenceEvent>>, length Time, loop, name BlobString}`
  + `triggerRequirements`, `sourceSubSequenceId`, `majorDirection`, `mask` **[refl]**. Every event type starts with
  `time` and `duration` (`ActionSequenceEvent`).
- **Sorted events [dis].** The window walker 0x34620 (called through 0x34520 by actor_spawn_entity, actor_pay_usage_cost,
  …) loops over the events (stride 12) and at `0x3474c: cmp rcx, rdi; jge 0x34801` returns at the first event whose
  time is ≥ the window's end. An event moved before earlier ones would never be inside a window when it is reached.
  Kiln keeps the list sorted: after time changes the sub-sequence's `events` header points at a stable-sorted copy of
  the 12-byte variant headers (offsets rebased to the same event data). **[rt]** "SpawnEntityEvent at 0.45 s (original
  0.65), PayUsageCost at 0.45; the 25 events stay sorted by time (the spawn is event 11 now)"; unload: back to 0.65.
- The registry (`ActionSequenceRegistry`, id map `+0x90` u32 FNV(GUID) → `ActorSequenceResource*`, Find 0x9327d0
  [0xe8b5a0]) holds pointers to the resources, so in-place edits reach it **[dis]** (research report, below).

## 4. Arsenals, buffs, perks, skills
- `EnemyArsenalRegistryResource {arsenals BlobArray<EnemyArsenalResource 32 B {guid, attacks, behaviors}>}`; attacks
  `AttackDesscriptionResource` (`description.{actionSequence, selectionChance, minRange, maxRange, threat, cooldownGroupID,
  …}`, constraint/command variant lists), rules `BehaviorDesscriptionResource` **[refl] [data]**. **[rt]** "arsenal of
  Enemy_Vukah_Melee attacks[0].description.maxRange 3.5 (original 2.5)".
- BuffTypeCollection `{buffMap BuffTypeMap 24 B, buffs BlobArray<BuffType 184 B>}`, `buffTypeId` u32 id;
  SkillTree `{nodes BlobArray<SkillTreeNode 140 B>, links}`, node `id` u32, `activeSkillUnlock` ItemId (the
  `Ability_DoubleJump` node is `0xffd5f940`: costs 2) **[refl] [data]**. **[rt]** "code: buff_cdbe9c80 defaultLifeTime
  10s (original 5s)", "skill node … costs 1 (original 2)", "Perk_Damage_Ice damageModifier.distribution.magicalIceDamage
  0.2 (x2)".

## 5. Clones
Static RE (Ghidra + capstone), confirmed in the lab for
templates.

### Templates **[dis] [rt]**
- The per-world template-info init `0x667c10` [0x9cbb80] lists every TemplateResource through ResourcesByType
  (`0x82a9a0`), resolves each (ByKey 0x829cd0, Data 0x829810) and calls **`RegisterTemplateInfo` 0x6671d0** [0x9cb140]
  `TemplateInfo* (holder = world + world.storage, const TemplateResource* data, const Guid*, bool force)` at 0x667dbd,
  then logs "[ecs%c] Initialized %,u template infos". It runs once per world, in its first ecs step (0x671910: `if (*R ==
  1 && init(R)) *R = 2`), not at app start.
- RegisterTemplateInfo: map `storage+0x745810` (HashMap GUID → TemplateInfo*, grows by itself), TemplateInfo array
  inline at `storage+0x280` `{data, count +8, cap +0x10 = 13000, grow callback +0x18}` (growing it would move the array:
  every TemplateInfo* would dangle), span arena `storage+0x2155e8` (cap 340,000 entries). `force` 0 returns an existing
  GUID's info unchanged. TemplateInfo (0xA8 B): GUID, interned name, family, component bitset, two spans of
  `{typeIndex, data pointer into the blob}`, own index `+0x98`.
- createEntity (0x623ce0) locks the resource by GUID (0x5f3aa0: ByKey + Data of `{guid, 0x39768775}`) and gets the info
  from the map; template-only components get the blob pointer in the entity's template slot, dynamic ones a copy.
- Kiln's clone: the base resource's bytes (self-contained: every offset in it is self-relative) with the name appended,
  `resource_put(NEW)` through kiln.assets (so the lock finds it, and later worlds' init registers it), then
  RegisterTemplateInfo on the world thread when `count < cap`, then the definition's fields as layers. Sigdb
  `gamedata/RegisterTemplateInfo` + `@…fields` capturing `tmpl.array` 0x280, `tarr.count` 8, `tarr.cap` 0x10 (unique on
  both exes: `sigdb_check.py` ok). **[rt]** "template Kiln_Test_Elite_Vukah (0b7f5a1e-…, clone of Enemy_Vukah_Melee)
  registered in the server world: TemplateInfo 00000000B2607FD8, 12270 of 13000 template infos";
  "spawned the clone Kiln_Test_Elite_Vukah 2544: GetTemplateComponent(Enemy01BehaviorState) = 0x7DB53148 = the
  clone's own data (0x7DB53148); its AI reads viewRange 40"; "the clone's own Level 20 and ScaleComponent 1.4";
  kiln.creatures' catalog: "1 not in the catalog, e.g. Kiln_Test_Elite_Vukah" (it reads the engine's map).
- kiln.spawn resolves a GUID against its engine template list read earlier and refreshes that list only for name
  lookups, so a clone is spawned by name until its GUID path refreshes too (requested change).

### Sequences **[dis] [rt]**
- `ActionSequenceRegistry` (systems +0x250), loader 0x9669f0, id map `+0x90` (u32 FNV(GUID) → `ActorSequenceResource*`;
  bits +0x98, cap +0xa0, keys +0xb8, values +0xd0, count +0xe4), Find 0x9327d0 [0xe8b5a0]. `ItemSequence {sequenceId
  u32 +0, sequence ActorSequenceReference +4, triggeredBy, …}`: `sequenceId` = FNV(sequence GUID) (Ability_DoubleJump
  `0xf8f5ecbe` = FNV(`e7b5dbb5-…`)) **[refl] [data]**.
- Kiln finds the id map without a fixed offset: the HashMap in the `ActionSequenceRegistry` singleton whose entries
  are the sequences' resource data, then inserts the clone (FNV(GUID) → kiln.assets' copy of the cloned resource,
  `resourceId` set to the new GUID). **[rt]** "the world's sequence registry: id map at +0x90, 2157 entries (2157
  checked against the resources)"; "sequence Kiln_Test_Fireball_Cast (2a9c4e71-…, clone of
  Attack_Player_Staff_FixedAnim_Fireball_fast) registered in the server world's sequence registry"; "Ammo_T1_Spell_
  Fire_Ball_lvl10 sequences[0].sequence -> 2a9c4e71-… (the clone), sequenceId 0x230a43e9 = FNV-1a of its GUID".
  That the game plays the clone when the item is used is a client check.

### Perks and buffs (server 26430253 / client 26430625, svn 1287369)
Evidence from the current exes (read-only copies), capstone over a full `.text` sweep, `tools/kfc` decodes of the
client's data, and a lab and a hosted client. Tags as above.

**Ids are FNV-1a of the GUID** **[data]**: in every ItemInfo, `perkIds[i]` = FNV-1a(`perkReferences[i]`) (6090 of 6090
filled slots), and every `BuffTypeReference` found in items and perks (35 GUIDs: `BuffImpactConfig.value`) hashes to a
`buffTypeId` of the collection. BuffType records store only the id; Kiln learns the game's buff GUIDs from those
references (configs, `ApplyBuffEvent.buffTypeOverride`, `onTimeout.followUp`).

**Perks** **[dis] [rt]**
- `PerkRegistry` is registered as the world singleton `"PerkRegistry"` (systems +0x298; the singleton list is built at
  server 0x235d0d, next to `"BuffRegistry"` = systems +0x248). PerkRegistryFind (server 0x93f8e0, client 0xe986a0) is
  the same engine HashMap find as the ActionSequenceRegistry's (0x9327c0 / 0xe8b680): map at +0x90, bits +0x98, cap
  +0xa0, keys +0xb8, values +0xd0, count +0xe4; values are the Perk resources' data.
- **[rt]** "the server world's perk registry: id map at +0x90, 365 entries (365 checked against the resources)";
  "perk Kiln_Test_Frostbite_Perk (9c4f2e81-…, id 0xb8349cd4, …) registered in the server world's perk registry (366
  perks)"; hosted client: the same in the client world's registry.
- A clone = the base Perk resource copied (self-contained blob; `debugName` re-pointed at the appended name, `name` /
  `description` LocaTagReferences = derived GUIDs) served by kiln.assets as NEW, then one map insert per world.

**Buffs** **[dis] [rt]**
- getBuffType (server 0x933700, client 0xe8c5c0; old 0x933710; unique pattern, sigdb `gamedata/GetBuffType`)
  `BuffType* (u32 id, BuffRegistry* r)`: `coll = *(r + 0x20)` (`gamedata/buffreg.collection`); bucket = `id &
  (buffMap.entries.count - 1)`; `entries[bucket]` = u64 {start, count}; scans `keys[start..start+count)`; `index =
  values[k]`; refuses `index > buffs.count` (`ja`: index == count passes, an off-by-one); returns `buffs + index *
  0xb8`. getBuffTypeByGuid (0x9335d0) hashes the GUID (0x40720) and does the same. No runtime map: the collection
  resource is the data.
- The other 0xb8-stride readers in `.text` (0x9336d0, 0x729a78: the save path's `isSaved` check, given the collection
  as an argument) are the same map lookup; none iterates `buffs` directly (searched: `imul …, 0xb8` near `+0x18` /
  `+0x1c` reads). The BuffRegistry loader (0x924380) resolves the BuffTypeCollectionResource (type 0x1be67cef) through
  the resource service.
- Kiln's mirror: the records copied to Kiln's memory near the collection (8 self-relative offsets per 184-byte record
  rebased), clone records appended, a new header + map (same bucket count while the load stays under 3/4), the
  registry's +0x20 swapped (InterlockedExchange; the slot is found as the qword holding the collection and checked
  against the sigdb constant). **[rt]** "Kiln's copy of the buff collection at … (274 game buffs, room for 256
  clones; 8 self-relative offsets per 184-byte record; the game's collection …, 512 map buckets)"; "the server world's
  BuffRegistry (… +0x20) reads Kiln's buff collection: 275 buffs (1 clones)"; "the game's getBuffType(0x9887fff3) in
  the server world = 000000007EC6C4F0 (the clone's record); a game buff 0xcdbe9c80 = 000000007EC60000 (Kiln's copy)".
- The game uses the clone: `Abilities.ApplyBuff` (the game's applyBuff native) on a spawned Vukah made a buff entity
  with `BuffNetworkData.buffTypeId` 0x9887fff3 and lifeTime 7 s (the clone's; the base 5 s), listed in the Vukah's
  `BuffContainer` **[rt]**. A code layer on a game buff (0xc0c267ae defaultLifeTime 3 → 9 s) applied with 9 s: the game
  reads Kiln's copy, which follows the game's records after each change **[rt]**.
- Client (hosted, 26430625): the client world's BuffRegistry swapped the same way; the buff list drew the clone's name
  from the replicated id **[rt]**.

#### Buff capacity recheck (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; dossier chapter 17.2 and caps VERIFY claims 3–4 confirmed.
The earlier 256-clone log above is historical. **[source]** Current `gamedata_clones.cpp` reserves **1024 clones**
(`kBuffRoom`), not an engine 10-bit index or per-entity buff limit. GetBuffType S `0x933700` / C `0xe8c5c0`
(`caps.buff.lookup` scratch guard, also resolves on old svn 1273043) consumes u32 ids/map indices and 0xb8-byte
records, collection at registry `+0x20`; id 0 is null. The `index==count` off-by-one acceptance remains, so generated
maps must never emit it. **[static]**

**[refl]** Collection header 32 B: entry/key/value BlobArrays `+0/+8/+0x10`, buff records `+0x18`; BlobArray is
`{i32 selfRelative,u32 count}`. BuffType fields: id `+0`, isSaved `+0x4a`, configurations `+0x60`, uiValues `+0x70`,
onCreate `+0x78`, whileApplied `+0x80`, onTimeout `+0x88`. BuffNetworkData is 24 B: Time `+0`, endOfLife `+8`,
**u32 BuffTypeId `+0x10`**, level `+0x14`; the wire identity is not a 1024-slot index. **[data]** Both archives contain
274 vanilla buffs / 512 initial buckets.

**[lab]** 1025 definitions yielded **1024 registrations and one safe refusal**, total 1298 records; 1024 real
GetBuffType self-checks returned clone records. No raised-quota run or large client-sync test was done. Mirror growth
is constrained by allocation, u32 counts/indices and **signed-i32 self-relative reach**, not just record count.
Published immutable headers/maps are retained for readers; a one-header byte estimate does not bound repeated
publication memory. The quota is Kiln policy; do not claim an arbitrary new cap or reclaimed live records.

**Tooling note** **[rt]**: clang-cl's `__try` covers calls, not plain loads in the guarded block: a scan that read a
junk map header's arrays directly (`map_copy`) faulted the tick despite its `__try`. Kiln now checks both ends of a
candidate map's arrays with `rd()` (a guarded copy through a call) before walking it.

## 6. In a client **[rt]**
- Hosted: the same registrations in the server world and, through the client world's own tick, in the hosted client
  world (each world has its own template table: "12270 of 13000" in both). Joined: the content state (27 changes,
  clone definition first) applied on the client; item tooltips follow the synced config value.
- A clone resource kiln.assets served for an earlier world stays served for the process, so the next world's registry
  lists it among the resources; Kiln now treats a GUID it built itself as its own.

## 7. Open questions
1. Whether the server sends template indices (`TemplateInfo+0x98`, `ecs::TemplateMessage`) to clients: if so, a client
   must register the same clones in the same order before it joins (the content sync registers them after the join).
2. What a vanilla client does with an entity of an unknown template (modSync refuses vanilla clients for mods with
   clones, so only relevant for misconfigured servers).
3. The client's cost prediction with a changed ManaCost (docs/re/abilities.md §8.1).
4. The arsenal's GUID → id step for `actionSequence` (not traced): decides whether an attack can point at a cloned
   sequence.
5. Span arena headroom at run time (`*(storage+0x2155f0)`); Kiln checks only the TemplateInfo count.

## 8. Lab results (lab `gamedata`) **[rt]**
```
gamedata-test done: 30 PASS, 0 FAIL                     (fresh; mods/tests/gamedata-test)
gamedata-test done: 4 PASS, 0 FAIL                      (reload gamedata-test: code layers off, the pack stayed)
OBSERVE … PushBackStrength: "Big" -> (absent)           (unload: every value back, the added config gone)
OBSERVE … events[SpawnEntityEvent#0].time: 0.650000013 -> 0.45   (load again)
gamedata-test done: 3 PASS, 0 FAIL                      (restart: the pack at world load, code re-applied)
sync: round trip: ok: 27 change(s) exported, 28 re-applied unchanged, 27 put back for a vanilla server, 4.6 ms
[mod:spelltweaks] Fireball I: mana 30 (vanilla 53), leaves the staff at 0.45 s (vanilla 0.65s), explosion radius 3.9 (vanilla 2.6), push-back Big; double jump stamina 5 (vanilla 10)
[mod:creaturetweaks] Vukah view range 27 m (vanilla 18; 11 Vukah templates changed); Kiln_Elite_Vukah (6b1e0c52-…) registered: level 20, scale 1.4, view range 36 m
```
A bug found on the way: on a re-application kiln.content drops the pack owners' layers but writes them out only at the
end, so paths that go through list headers (added configs, sorted events) were resolved in stale memory and the add
was lost; Kiln now flushes before it resolves.

## 9. Action follow-ups versus pose exits (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; dossier chapter 16c, blocks-a VERIFY claims 11–12 confirmed. [refl] [data]
ActorSequenceResource 0x18 B: GUID `+0`, subSequences `+0x10`; ActorSubSequenceResource 0x48 B: events `+0`,
length Time `+8`, loop `+0x10`, name `+0x14`, requirements `+0x20`, source GUID `+0x30`, direction/mask `+0x40/+0x41`.

`SetActionOportunity` (engine spelling) is 0xd8 B/align8: time/duration `+0/+8`, required tag GUID `+0x10`,
forbidden tags[4] `+0x20`, trigger **u16** `+0x60`, required/forbidden skill GUID `+0x64/+0x74`, followUpTime
signed ns `+0x88`, eventGuid `+0x90`, override `+0xa0`, **followUp GUID `+0xa8`**, costImpact `+0xb8`, values `+0xc8`.
ClearActionOportunity is 0x68 B. Parry **input=9**, Parried **outcome=23**, ParriedOverride=26; do not conflate them.
A single trigger may have several skill-conditioned rows with different follow-up times. Empty-GUID evade/cancel
opportunities are not the same as nonempty sequence links. [source] The existing rewriter intentionally skips them.

The 2,944-node player AnimationGraphResource2_0 uses fromState/toState transition nodes for **pose exits**, not next
weapon sequence selection. Weapon ItemInfo.sequences chooses entry; timed opportunities choose follow-up. Held
Ability_Block has loop=false but a timed LoopEvent; setting the subsequence loop flag is not equivalent. Staff cast
can combine overlay/full-body events and loops, not a single generic primary pose. [source] Retiming only the clip leaves
sequence/impact/followUpTime windows unchanged. No new native hook is needed to expose existing resource links;
full examples, route policy and compiler-extension-only chapter 16d remain in the dossier.
