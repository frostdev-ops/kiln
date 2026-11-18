# Gems

Enshrouded's gems for mods, in one guide: what a gem is in the game, how to change one with a data pack, the `Gems`
API in C# (the catalog, the gems a player's equipment runs, sockets, gem events), new gems with their own effects, the
Shroud Forge's actions from code, and sockets on rings, armour and shields. Three parts have a page of their own for the
details; this page says what each part is and how they fit together. The game-side evidence for the forge (its
handler, layouts, signatures) is in [docs/re/gems-forge.md](../../re/gems-forge.md).

| Part | Where | API | Row |
|---|---|---|---|
| How gems work, the game's gems, data packs | this page | kiln.gamedata's `items` section | — |
| Reading gems | this page, "Reading gems from C#" | `Gems.Catalog`, `Get`, `Equipped(player)`, `Socket(item)` | `gems.read` |
| Gem events | this page, "Events" | `GemTriggeredEvent`, `GemSocketChangedEvent` | `gems.triggers`, `gems.read` |
| New gems | this page, "New gems"; [gems-new.md](gems-new.md) | `Gems.Register`, `GemDefinition`, `GemTrigger`, `GemTriggerContext` | `gems.triggers` |
| Forge actions | this page, "Forge actions"; [gems-forge.md](gems-forge.md) | `Gems.Socket(player, ...)`, `Unsocket`, `Upgrade`, `Recycle`, `GemCraftingEvent`, `GemCraftedEvent` | `gems.craft` |
| Sockets on other gear | this page, "Sockets on other gear"; [gems-sockets.md](gems-sockets.md) | `Gems.AllowSockets`, `Gear.GemSlot`, `Gems.AddSocket`, `Gems.ReturnSocketedGems` | `gems.sockets` |

| Layer | Path |
|---|---|
| Managed | `managed/services/Kiln.Abilities/Gems/` → `kiln/host/Kiln.Abilities.dll` (static class `Kiln.Gems`, its types and events); Kiln.Items (`Gear.GemSlot` on other gear); Kiln.Ui (the forge lists on clients) |
| Native | kiln-items.dll: `kiln.gemcraft` (the forge actions, payers, offered chest gems; `kiln_gemcraft.h`) and `kiln.items` v7 (sockets on other gear, the re-creating give, socket rolls); kiln.ui.values `gemforge_sockets` (client); kiln.content (an item's program served through kiln.assets; placeholders) |
| Reads | kiln.items (item catalog, item instances), kiln.gamedata (a gem's `equipment.program` and `impactValues`), kiln.world + kiln.types (`Equipment.equipmentImpacts`), kiln.abilities (native events, `kiln.Signal`) |
| Signatures | `sigdb/symbols/{server,client}.d/gems-forge.json`, `gems-sockets.json` |
| Rows | `gems.read`, `gems.triggers`, `gems.craft`, `gems.sockets` ([caps.md](caps.md)); see "Capability rows" below |
| Tests | see "What is checked" |

Since 0.2.0-dev.29.11 (mod API 0.2.5), every part of this page.

## How a gem works
A gem is an item of its own (category `WeaponGem`), with its own item entity and a **level** from 1 to 105. It is not
a perk and not a buff: its ItemInfo names an **Impact program** (`equipment.program`) and the **configs** that
program reads (`impactValues`: damage, buff types, durations, chances). Scaled configs grow with the gem's own level
(function `WeaponPowerCreep`, source `Self`, a `scaleFactor` per config).

An item with a socket keeps the gem in its `WeaponGemSlot` (the gem item, its level, its entity). In the game only
weapons get one; with "Sockets on other gear" below, rings, armour, shields and other gear can have one too. Every step,
the game's `equipment_impacts` looks at the player's 11 impact slots (the held item, the shield, five armour pieces, two
rings, ammo, the spell) and, for an item with a gem in its socket, spawns one **gem impact**: an entity that runs the
gem's program with the gem's configs and level. That impact lives while the item stays in its slot and the gem stays in
the socket. A gem whose item names no program runs nothing (the game logs "Gem has no valid Impcat Program assigned").

Most gem programs loop over the **wearer's** hit events each step and react to a condition: a critical hit, a killing
blow, a backstab, a blocked hit, several hits in a row, sprinting, being hit. They then apply a buff, deal damage or
spawn something. Two are passive (Aerobatics, Hyperspecialization). A gem reacts to every hit of its wearer, not only
to the hits of the weapon that carries it; most skip hits from skills and other gems.

```
gem item (ItemInfo, category WeaponGem, its own item entity with a Level 1..105)
  ├─ equipment.program → an Impact program (27 programs for the 29 gems: the three Necromancy gems share one)
  ├─ impactValues      → the program's configs, scaled by the gem's level
  └─ uiValues          → the tooltip rows
carrier item entity ── WeaponGemSlot { gem item, level, gem entity }   (a weapon; any gear with sockets on other gear)
equipment_impacts (server, every player, every step)
  for each of the 11 impact slots: the item's own impact, its unlocked perks, its socketed gem
    gem → a gem impact { the gem's program, configs copied from the gem's ItemInfo, the gem's level }
```

**The Shroud Forge** sockets (Slot), unsockets (Unslot), upgrades (+1 level for Archaic Essence, up to the forge's
`maxGemLevel`: 10 at the Grassland forge with Archaic Essence I, up to 50 at the Ancient Lands one) and recycles (the gem
is destroyed for essence). A gem above every forge's cap (a level 55 gem from loot) can be socketed but not upgraded.
Mods run the same four actions from code ("Forge actions" below).

## The game's gems
The config ids are what a data pack writes (`impactValues.0x...`); the name after an id is the program's own variable
name where it has one, which kiln.gamedata also takes as an alias (`impactValues.Scaled_Damage_Cut`) when no other
config shares it. Values are the base values at level 1; "scale" is the config's `scaleFactor`. Read them live with
`Gems.Catalog` (they follow your data packs; `GemConfigValue.Alias` there is the config vocabulary's alias for the id,
such as `Buff1` for `0x5adb2644`). From the game's data (client 26430625): the dedicated server 26430253's item
registry has 28 of these, without the Gem of Necromancy (`T5_Gem_GemOfNecromancy`; lab 2026-11-16).

Scaling is not linear in the level the way the numbers suggest: the Gem of Puncturing (`0x5e34f09b` 3, scale 2.7) runs
with 5 at level 5, and with the base doubled to 6 it runs with 8 (lab). Read the value a running gem uses from its
impact (`ImpactStartedEvent.Instance`, `Find(0x5e34f09b)`), not from the table.

| Gem | Item | Rarity | Program | Configs |
|---|---|---|---|---|
| Gem of Bleeding | `T1_Gem_GemOfBleeding` | Common | `b199646f-31a1-495e-b3d1-d5fef2fcaab3` | `0x5adb2644` BuffType (Buff) e9fad497-c3f8-4741-9c50-64378084717d<br>`0xe21b4b72` (Time) 0.5 s<br>`0x854ea8fa` (Float) 0.5<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 8, scale 2.6<br>`0x3ff81f2b` (ScaledTime) 6 s, scale 0.5 |
| Gem of Burning | `T1_Gem_GemOfIgnition` | Common | `a742438c-7493-4c2b-aaf6-12ead0a8c161` | `0x2fd280c2` CooldownDebuffDuration (Time) 5 s<br>`0xbdda99dd` CooldownBuff (Buff) 61a45cab-fc33-41fe-9c69-07d31712004e<br>`0x5adb2644` BuffType (Buff) 00800346-2204-45cc-b879-189c909c7374<br>`0x56c7b279` CooldownDelayBuff (Buff) 3e7c0d3f-a4c8-4381-b01a-fa59d934dbcb<br>`0x854ea8fa` (Float) 0.3334<br>`0x8fbfcd98` (Time) 0.333 s<br>`0x3ff81f2b` (ScaledTime) 3 s, scale 0.5<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 6, scale 4.5 |
| Gem of Puncturing | `T1_Gem_GemOfRepetition` | Common | `ec9a11bd-3813-4ca8-af2d-55132fd2af55` | `0x81456618` (Buff) 62e197d6-e379-48ec-b64d-af0cc8ec0050<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 3, scale 2.7<br>`0x3ff81f2b` (ScaledTime) 3 s, scale 0.333 |
| Gem of Skulls | `T1_Gem_GemOfSkulls` | Uncommon | `8611b73a-2b76-4745-bbbf-222f87cf0199` | `0xf6a785cd` SpawnTemplate (Template) d39d7329-c9f5-43ee-b070-0de8b9223ea5<br>`0x1ef169b2` Scaled_LevelOverwrite (ScaledUint32) 1, scale 6.67<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.3, scale 0.0666 |
| Gem of Fracturing | `T2_Gem_GemOfFracturing` | Rare | `c038864f-f8d6-4f71-b395-f0412f070ef7` | `0x29aa2976` SpawnTemplate_Post (Template) 2575a534-150b-43f2-b503-3c04c5f1e371<br>`0xf6a785cd` SpawnTemplate (Template) a7b066df-f004-44ad-b0ea-7ffb52a26cde<br>`0xde1695c2` ChanceValue (Float) 0.5<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.3333, scale 0.0225<br>`0x5e34f09b` (ScaledSint32) 8, scale 8 |
| Gem of Hopeful Embers | `T2_Gem_GemOfHopefulEmbers` | Rare | `7118612b-f3aa-4b12-a057-866f33a14532` | `0xa1e86444` Entity (Template) d63e4932-693c-4d69-b762-19278e5313f9<br>`0x955055b2` (ScaledSint32) 20, scale 5<br>`0x6fd0dcf1` FogResistance (ScaledUint32) 120, scale 24 |
| Gem of Retaliation | `T2_Gem_GemOfRetaliation` | Common | `880dcc04-ab61-4122-8621-69331d6efd80` | `0xf6a785cd` SpawnTemplate (Template) 4fa24d06-1b1e-48f7-8edd-51aac26a30b4<br>`0xbee6d4a9` PrimerBuffDuration (Time) 3 s<br>`0x81456618` (Buff) 62e197d6-e379-48ec-b64d-af0cc8ec0050<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.33, scale 0.0222<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 15, scale 10 |
| Gem of Swiftness | `T2_Gem_GemOfSwiftness` | Uncommon | `a268e37e-685d-4282-820f-10415312a2f5` | `0x5adb2644` BuffType (Buff) 8739029e-bcf7-41bd-8d58-0f2210759624<br>`0xda684468` PrimerBuffType (Buff) 96467037-c368-4d04-bc06-61222d472d9b<br>`0xbee6d4a9` PrimerBuffDuration (Time) 5 s<br>`0x4684c4f4` PrimedBuffType (Buff) 15357d7b-e1d4-482f-bf8f-791fc933672c<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 4, scale 4 |
| Gem of Toxic Betrayal | `T2_Gem_GemOfToxicReaction` | Uncommon | `21c8734e-067e-4943-9f6f-8fed5505f254` | `0x81456618` Debuff1 (Buff) 2e9b8d04-21ba-407a-92fd-39c4effd5930<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 3, scale 2<br>`0x3ff81f2b` (ScaledTime) 6 s, scale 1 |
| Gem of Agility | `T3_Gem_GemOfAgility` | Rare | `914b5973-0858-4213-a622-4fec0a7b8d92` | `0xbee6d4a9` PrimerBuffDuration (Time) 5 s<br>`0x5adb2644` BuffType (Buff) 569ff513-a367-4794-a2ee-0a74d78f6e94<br>`0xf6a785cd` SpawnTemplate (Template) 9ff81751-98fe-44bb-b2af-ffdbf532b492<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 3, scale 3<br>`0x5d8dc70d` (ScaledSint32) 15, scale 7.5 |
| Gem of Storm's Eye | `T3_Gem_GemOfConduction` | Uncommon | `74549233-27d5-4f73-bcb3-5766cbb58209` | `0xbee6d4a9` PrimerBuffDuration (Time) 5 s<br>`0x2fd280c2` (Time) 1 s<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 4, scale 4 |
| Gem of Lesser Necromancy | `T3_Gem_GemOfLesserNecromancy` | Uncommon | `4694bf1e-feda-4064-9843-8880ac74e10f` | `0xf6a785cd` SpawnTemplate (Template) c9c957ad-78bf-44fe-a891-b0771577a3b3<br>`0x952ec6b4` WaitDuration (Time) 3 s<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.1, scale 0.0667<br>`0x1ef169b2` Scaled_LevelOverwrite (ScaledUint32) 1, scale 6.67 |
| Gem of Overpowering | `T3_Gem_GemOfOverpowering` | Uncommon | `426fa6cd-5aec-43f4-993e-51198fc4d9a4` | `0x17920fcc` PushBackStrength_Overpowering (PushBackForceConfig) Big<br>`0x8c622a97` StaminaDamageValue (ScaledSint32) 10, scale 14 |
| Gem of Corruption | `T4_Gem_GemOfCorruption` | Epic | `b6557bf7-051b-4be5-bed5-d6d43e9ed0e7` | `0xcec0a96e` BuffTypeTier3 (Buff) bd1b66b4-539f-4477-8b66-d0b815ff43fe<br>`0xb20c7aa1` BuffTypeTier5 (Buff) 89d01d83-e2a5-405f-b9c5-6dbd12a0cc06<br>`0xbee6d4a9` (Time) 3 s<br>`0xb93c6a85` BuffTypeTier1 (Buff) 0dfc3483-53de-4953-b600-a2a6806c0cf7<br>`0x541dea90` BuffTypeTier2 (Buff) 1d71ce39-6c25-4989-b1df-f4028cc4c9ec<br>`0x6fc73919` BuffTypeTier4 (Buff) 69c30209-70ca-45e2-86c8-158909ac8c38<br>`0x63d242a0` (Float) 0.5<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 5, scale 5 |
| Gem of Focused Light | `T4_Gem_GemOfFocusedLight` | Legendary | `04e5c47a-6e5f-47fd-9fc7-3da0bbe7b0f2` | `0x541dea90` BuffTypeTier2 (Buff) a00dbdc0-0394-43c5-83cb-96c6c425dec9<br>`0xdd9e684` (Float) 4.5<br>`0x6f307db7` AttackModTier1 (Float) 1<br>`0xcec0a96e` BuffTypeTier3 (Buff) a2aa64fb-bce0-4301-8b5a-6bba911b9da2<br>`0xb8f00949` AttackModTier2 (Float) 2.25<br>`0xb93c6a85` BuffTypeTier1 (Buff) 7ab44006-58e5-41e2-ac53-d19d5d80884f<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 10, scale 10 |
| Gem of Battle Mania | `T4_Gem_GemOfObsession` | Rare | `a3bf7ff3-3edb-44aa-b66c-6c2ec3285bf8` | `0xbee6d4a9` PrimerBuffDuration (Time) 3 s<br>`0x130a0ef2` (Time) 10 s<br>`0x581f75ff` (Sint32) 10000<br>`0xb58d778a` (ScaledUint32) 25, scale 10<br>`0x62acdc61` (ScaledUint32) 15, scale 7.5<br>`0x867736f3` (ScaledUint32) 10, scale 5 |
| Gem of Soul Reaping | `T4_Gem_GemOfSoulReaping` | Epic | `e81a48da-5a9d-4b85-94c3-505f8d8e21a0` | `0x130a0ef2` DebuffDuration (Time) 62 s<br>`0x952ec6b4` (Time) 0.3 s<br>`0x9106dde8` ScaledValue_HealthGain (ScaledSint32) 5, scale 33.3<br>`0x1ef169b2` (ScaledUint32) 1, scale 6.67 |
| Gem of Bloodlust | `T5_Gem_GemOfBloodlust` | Rare | `9a8149fe-8506-4061-bf40-f9d1798c4239` | `0xde1695c2` ChanceValue (Float) 0.05<br>`0xbee6d4a9` (Time) 10 s<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.1, scale 0.0667<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 4, scale 4 |
| Gem of Cold Composure | `T5_Gem_GemOfColdComposure` | Legendary | `5d4ad2e3-1eef-47fe-af7c-51a76abe7865` | `0xf6a785cd` SpawnTemplate (Template) 09bc012d-60f2-464a-9c06-977b413e8c1a<br>`0x5adb2644` BuffType (Buff) a1e254d7-7da2-40f5-a999-5a283d820539<br>`0xda684468` PrimerBuffType (Buff) ad7fe9cd-cf71-412d-9a58-5d50227cfbb8<br>`0xbee6d4a9` PrimerBuffDuration (Time) 1.6 s<br>`0x65d4559d` (Float) -0.2999<br>`0x2b9400b4` (ScaledSint32) 20, scale 10<br>`0x2ec994d1` (ScaledSint32) 20, scale 10<br>`0x5e34f09b` (ScaledSint32) 10, scale 10 |
| Gem of Meteoric Impact | `T5_Gem_GemOfMeteoricDescent` | Epic | `5fcdf151-4a4a-4ab1-9c60-cc7f685cc3bd` | `0x536ed7da` StandardSpawnTemplate (Template) 896758dd-f2d5-4e26-89f0-a4856f489e77<br>`0x3f80a690` SpawnDelay1 (Time) 0.2 s<br>`0x130a0ef2` (Time) 3 s<br>`0x4684c4f4` PrimedBuffType (Buff) 70512cab-7a86-4a3f-ad63-4b0e2f88b44d<br>`0xda684468` PrimerBuffType (Buff) d21505fb-ff16-4046-b067-f1e88760637d<br>`0x29aa2976` SpawnTemplate_Post (Template) 5f8755f3-f632-4060-94dd-0329b8dba8f7<br>`0xaa1f9d8c` PostWaitTime (Time) 0.2 s<br>`0x5adb2644` BuffType (Buff) 819ed304-ff5b-48fa-93bb-f898660160cd<br>`0xfaf3b41b` (ScaledSint32) 3, scale 3<br>`0xa8338df8` (ScaledSint32) 7, scale 7<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 3, scale 3<br>`0xc9c2d7f1` ScaledValue_BuffDuration (ScaledTime) 5 s, scale 3.33 |
| Gem of Necromancy | `T5_Gem_GemOfNecromancy` | Rare | `4694bf1e-feda-4064-9843-8880ac74e10f` | `0xf6a785cd` SpawnTemplate (Template) 10f892be-2374-4d31-ae27-85e0a36f3853<br>`0x5dc5e6d7` (EntityTag) 42ba19f1-22a3-4794-9642-756661f61fa0<br>`0x952ec6b4` WaitDuration (Time) 5 s<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.1, scale 0.0334<br>`0x1ef169b2` Scaled_LevelOverwrite (ScaledUint32) 1, scale 6.67 |
| Gem of Aerobatics | `T6_Gem_GemOfAerobatics_UnderFog` | Epic | `33dbcd32-1c8c-413c-8747-34f42ee21d7a` | `0xe21b4b72` WaitTime (Time) 1 s<br>`0x5e34f09b` (ScaledSint32) 2, scale 2<br>`0x50c9b633` (ScaledUint32) 1, scale 0.667 |
| Gem of Currents | `T6_Gem_GemOfCurrents_OverFog` | Rare | `a9ad7fcd-e2cc-43f7-9447-198a93ed4b56` | `0x43101755` Debuff2 (Buff) 5805f59f-250a-4789-a21c-6dd02d8223f2<br>`0x81456618` Debuff1 (Buff) caf3c0ad-58f0-4513-aea8-40aa38f44f6b<br>`0x5e34f09b` Scaled_Damage_Cut (ScaledSint32) 8, scale 1.5<br>`0x3ff81f2b` (ScaledTime) 6 s, scale 0.5<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.33, scale 0.0222 |
| Gem of Greater Necromancy | `T6_Gem_GemOfGreaterNecromancy` | Rare | `4694bf1e-feda-4064-9843-8880ac74e10f` | `0xf6a785cd` SpawnTemplate (Template) ef0c170d-0653-4517-88a7-0c4c9aba8139<br>`0x5dc5e6d7` (EntityTag) 42ba19f1-22a3-4794-9642-756661f61fa0<br>`0x952ec6b4` WaitDuration (Time) 10 s<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.1, scale 0.0334<br>`0x1ef169b2` Scaled_LevelOverwrite (ScaledUint32) 1, scale 6.67 |
| Gem of Lycanthropy | `T6_Gem_GemOfLycanthropy` | Legendary | `e9652fd8-2d7a-4ace-b913-eae47cf178b3` | `0xe38f6439` (ScaledSint32) 27, scale 17<br>`0x958958bc` (ScaledFloat) 0.01, scale 0.067<br>`0x9106dde8` (ScaledSint32) 10, scale 12<br>`0xc9c2d7f1` (ScaledTime) 60 s, scale 6.67<br>`0x766757f5` (ScaledSint32) 25, scale 40<br>`0xe395f2c8` (ScaledSint32) 10, scale 5<br>`0xe6ddff32` Scaling_ChanceValue (ScaledFloat) 0.16666, scale 0.0225<br>`0x3548f806` (ScaledSint32) 27, scale 9 |
| Gem of Armoring | `T7_Gem_GemOfArmoring` | Legendary | `1f2c95a6-feaa-4ae2-8cb1-f8377913e5f6` | `0x5adb2644` (Buff) 090a7daa-32ea-4adb-b0c5-edb7b40f189a<br>`0xc9c2d7f1` (ScaledTime) 1 s, scale 0.367 |
| Gem of Cleansing | `T7_Gem_GemOfCleansing` | Epic | `6865cb46-7fa6-4871-a1fc-6af7d950df7c` | `0x81456618` Debuff1 (Buff) 4b148824-e019-42e6-a1b2-5dfab19b3292<br>`0x3ff81f2b` (ScaledTime) 3 s, scale 0.667 |
| Gem of Destiny | `T7_Gem_GemOfDestiny` | Epic | `b944bfeb-ba18-4c99-9d0d-79c7cabd1e08` | `0x130a0ef2` (Time) 20 s<br>`0x6c2fad6d` (Float) 25<br>`0x9106dde8` (ScaledSint32) 10, scale 12<br>`0xe395f2c8` (ScaledSint32) 10, scale 5<br>`0x5d8dc70d` (ScaledSint32) 15, scale 7.5 |
| Gem of Hyperspecialization | `T7_Gem_GemOfHyperSpecialization` | Legendary | `555230ae-2532-4ed6-be23-51e9308f64b4` | `0xbee6d4a9` PrimerBuffDuration (Time) 3 s<br>`0xdf316161` (ScaledSint32) 10, scale 3.33 |

## Changing a gem with a data pack
kiln.gamedata's `items` section changes any gem's configs (and with `@refs`, its program). Selectors take the debug
name, the GUID, the `0x` id or a glob (`"*_Gem_*"`); values take ops on the game's original value (`mul`, `add`,
`min`, `max`, `set`, `reset`). A config the gem does not have is added. Matching tooltip rows (`uiValues`) follow.

```json
{ "format": 1, "items": {
    "T1_Gem_GemOfBleeding": { "impactValues.0x5e34f09b": { "mul": 2 },
                              "impactValues.0x3ff81f2b": 10 },
    "T5_Gem_GemOfNecromancy": { "impactValues.0x952ec6b4": 8 } } }
```

The first line doubles the Gem of Bleeding's cut damage, the second makes its bleed last 10 seconds at level 1 (the
dossier's `DebuffDuration`), the third sets the Gem of Necromancy's `WaitDuration` to 8 seconds (5 in the game). The three Necromancy gems share one program: change their configs,
not the program. From C#: `Content.ItemData.Get("T1_Gem_GemOfBleeding")!.Config("0x5e34f09b").Set(16)`
([gamedata.md](gamedata.md)).

**When it takes effect.** The game copies a gem's configs into its gem impact once, when the impact is spawned: when
the item is equipped, when the hotbar switches to the weapon, when the socket's gem changes. A gem that is already
running keeps its old values. Tell players to **switch the hotbar away from the weapon and back** (or re-equip it)
after a change.

**Who sees what.** The effect runs on the server, so it is right for every player, also with a vanilla client. The
tooltip numbers are the client's own: Kiln clients get the synced values, vanilla clients show the old ones.

Per player and per moment (not for everyone): hold the gem programs (`Abilities.HoldPrograms`) and change one
instance in `ImpactStartingEvent` ([abilities.md](abilities.md)). `Gems.Get(gem)!.Program` gives the program's GUID;
`Abilities.IdOf(guid)` its id.

## Reading gems from C#
All members are tick thread and need the row `gems.read` (`KilnUnavailableException` naming it otherwise).

```csharp
if (!Capabilities.Is("gems.read")) return;
foreach (var g in Gems.Catalog)
    Log.Info($"{g.DisplayName ?? g.Name}: {g.Rarity}, program {g.Program}, {g.Configs.Count} configs{(g.FromMod ? " (mod)" : "")}");

foreach (var eq in Gems.Equipped(player))                         // the gems running for this player now
    Log.Info($"{eq.Slot}: {Gems.Get(eq.Socket.Gem)?.DisplayName} level {eq.Socket.Level} in {eq.Socket.Carrier}");

if (Gems.Socket(Items.Held(player)) is { } s)                      // the gem in one item's socket
    Log.Info($"held weapon: {s.Gem} level {s.Level}");
```

| Member | What it gives | Where |
|---|---|---|
| `Gems.Catalog` | every gem item (`GemInfo`): item, name, display name (a game gem's English name; a mod's gem: the name its definition gives, `displayName` or its English text; null without one), rarity, level range, program GUID, configs by id (`GemConfigValue`: id, alias, kind, base value, scale factor, `Text` for a GUID or enum value), `FromMod` (a mod's item: kiln.content's definitions or a kiln.gamedata clone). Read again when the catalog or game data changed, at most every 2 s otherwise | server, and a game client with a world |
| `Gems.Get(item)` | one gem's `GemInfo`, null when the item is not a gem | as above |
| `Gems.Equipped(player)` | the gems whose effects run now, one `EquippedGem` per impact slot with a gem impact: slot (`GemImpactSlot`: `Held`, `Shield`, `Head`..`Feet`, `Ring1`, `Ring2`, `Ammo`, `Spell`), the socket (`SocketedGem`: carrier, gem, level) and the gem impact's entity | server (and a hosting client): a joined client has no gem impacts |
| `Gems.Socket(item)` | the gem in an item's socket (`SocketedGem`), null when it has none | server (and a hosting client) |

`Gems.Socket(item)` with one argument reads; `Gems.Socket(player, carrier, gem, forge)` is the forge's Insert ("Forge
actions" below).

`Equipped` reads `Equipment.equipmentImpacts[slot]` (`weaponGemImpactEntityId`) through kiln.world, at the layout the
game's reflection gives on the running build. A slot is listed when the game spawned the gem's impact, which is when the
effect runs: a socketed weapon in the backpack is not listed, the held one is. The carrier comes from item instances
(`ItemInstanceRef`); when it cannot be read the gem and level come from the gem's own entity and the carrier is
`ItemInstanceRef.None`. A gem without a valid program (kiln.gamedata does not know it) has `Program` empty and no configs.

## Events
| Event | When | Fields |
|---|---|---|
| `GemTriggeredEvent` | a gem did something: its impact ran `applyDamage`, `applyBuff`, `spawnEntity` (or a native passed to `Abilities.Natives.Observe`). Posted right after the matching `ImpactNativeEvent`, one Pre after the call | `Player`, `Gem` (`EquippedGem`), `Native` (the `ImpactDamageEvent` / `ImpactBuffEvent` / `ImpactSpawnEvent`), `Target` (the damage's or buff's target, the spawned entity), `Step`, `Info` |
| `GemSocketChangedEvent` | a socket of one of a player's items changed, however it happened | `Owner`, `Carrier`, `Before`, `After` (`SocketedGem?`), `Change` (`Socketed`, `Unsocketed`, `LevelChanged`, `Removed`), `Step` |
| `GemCraftingEvent` | a player is about to use a forge (cancelable); "Forge actions" | `Player`, `Forge`, `Action`, `Carrier`, `Gem`, `GemLevel`, `Cost`, `Yield` |
| `GemCraftedEvent` | a forge action happened: a player's, a `Gems` call, `Gems.ReturnSocketedGems`; "Forge actions" | `Result` (`GemCraftResult`), `ByCode`, `Paid`, `Received`, `LevelBefore`, `LevelAfter` |

```csharp
[Subscribe]
void OnGem(GemTriggeredEvent e)
{
    if (e.Native is ImpactDamageEvent d && Gems.Get(e.Gem.Socket.Gem)?.Name == "T1_Gem_GemOfBleeding")
        Log.Info($"{e.Player}'s Bleeding gem (level {e.Gem.Socket.Level}) hit {e.Target}");
}

[Subscribe]
void OnSocket(GemSocketChangedEvent e) => Log.Info(e.ToString());   // "1's 0x...: Socketed (empty -> 0x... level 12, ...)"
```

`GemTriggeredEvent` maps a native event's impact to a gem through the root player's `Equipped` gems, read once per
player and step: the impact itself, or an impact the gem spawned (its `ImpactHierarchy` parent chain, up to three
steps up). The gems are read when the event is posted, one Pre after the call: a native of a gem impact that ended
in between (the hotbar switched away on that step) is posted as `ImpactNativeEvent` only. Subscribing turns on
kiln.abilities' native events as `ImpactNativeEvent` does. It is observe only: to change what a gem does, use a data
pack, `Abilities.Natives.Wrap` / `Abilities.OnDamage` filtered on the gem's program id, or `ImpactStartingEvent`. A gem
reacts only to hits and states: a block or a special attack of the weapon has no gem signal of its own.

`GemSocketChangedEvent` comes from reading players' items in turns (two players a step, about every `players / 2`
steps for each), only while someone subscribes. It does not say which forge or what it cost (`GemCraftedEvent` does),
comes a few steps late, and misses a change undone between two reads. What counts:
- `Socketed`: the socket got a gem (empty before, or another gem: `Before` is the old one);
- `Unsocketed`: the socket is empty and a gem of that kind came back to the player's inventory or went into another of
  their sockets (Kiln counts the gems of each kind that came back; each return is one `Unsocketed`, the rest `Removed`);
- `LevelChanged`: the same gem at another level (the forge's Upgrade);
- `Removed`: the gem is gone: it left the socket without coming back (the forge's Recycle), or the carrier was
  destroyed with it.
A carrier that leaves the player with its gem (into a chest, to another player) and an item first seen with a gem
(picked up, crafted) are not changes, nor is anything about a player first seen (joined, back after leaving, in a new
world): their changes count from their next read. Without a server world (a game client's menu, a joined session)
nothing is read or posted.

Without the `Gems` events, `ImpactStartedEvent` / `ImpactEndedEvent` (a gem impact starts on equip and on a hotbar
switch, ends when the item leaves the slot) and `ImpactNativeEvent` filtered on the gem program's id
(`Abilities.IdOf(Gems.Get(gem)!.Program)`) still work ([abilities.md](abilities.md)).

### Which event for what
One change can show up in more than one event; they never disagree, they see it from different places:

| What happened | `GemCraftedEvent` | `GemSocketChangedEvent` (polled, a few steps later) |
|---|---|---|
| Insert (a player's, or `Gems.Socket`) | `Socket`, one step after | `Socketed` |
| Remove (a player's, `Gems.Unsocket`, or `Gems.ReturnSocketedGems`: `Forge` none, nothing paid) | `Unsocket` | `Unsocketed` |
| Upgrade of a socketed gem | `Upgrade`, `LevelBefore` / `LevelAfter` | `LevelChanged` |
| Upgrade of a loose gem | `Upgrade` | nothing (no socket changed) |
| Recycle of a socketed gem | `Recycle`, `LevelAfter` 0, `Received` | `Removed` |
| Recycle of a loose gem | `Recycle` | nothing |
| A mod writes a socket, moves items, destroys a carrier | nothing | `Socketed` / `Unsocketed` / `LevelChanged` / `Removed` |
| `Gems.AddSocket` (a new item entity with an empty socket) | nothing | nothing (an item first seen is no change) |

Two forge actions that fall between two reads of a player come out of the poll as one change (an Insert and an Upgrade:
one `Socketed` with the new level). A player's action that a `GemCraftingEvent` handler canceled changes nothing and
posts neither.

`GemTriggeredEvent` is about effects, not sockets: a gem impact's `applyDamage`, `applyBuff`, `spawnEntity`. A new gem
with a C# handler (`Gems.Register` with a `Trigger`) does its work in the handler; its trigger program sends
`kiln.Signal` and calls none of those natives, so it posts no `GemTriggeredEvent` (lab-checked). What the handler itself
does (`Abilities.ApplyBuff`, `Combat.Damage`) is not the gem impact's either.

## New gems
Full reference: [gems-new.md](gems-new.md). A new gem is an item: a kiln.content clone of a game gem with its own GUID,
name, texts, icon, rarity and level range. The forge lists, sockets, upgrades and recycles it like a game gem. Its effect
is one of three:

1. the base gem's program with the gem's own numbers (a data pack alone can do this: `"items": {"add": [...]}`);
2. another loaded Impact program (another gem's, a perk's, one a mod cooked with `tools/impact` and serves through
   kiln.assets) with the gem's own numbers;
3. a **C# handler**: `Gems.Register` gives the gem one of Kiln's six trigger programs (`GemTrigger.OnHit`,
   `OnHit(crit: true)`, `OnKill`, `OnBackstab`, `OnHitTaken`, `Passive(interval)`), and the handler runs on the server's
   tick thread, as the registering mod, one step after the hit, with a `GemTriggerContext` (wearer, target, gem, level,
   carrier, impact slot, damage, crit / kill / backstab / blocked).

```csharp
public override void OnLoad()                        // every side that has the mod: clients need the item to show it
{
    var gem = Gems.Register(new GemDefinition
    {
        Id = "mymod:reaper", BaseOn = "T1_Gem_GemOfBleeding", DisplayName = "Gem of the Reaper",
        Trigger = GemTrigger.OnKill(),
        OnTrigger = ctx => Log.Info($"{ctx.Player} killed {ctx.Target} with a level {ctx.Level} gem in {ctx.Slot}"),
    });
    if (!gem.Ok) Log.Warn(gem.Reason!);
}
```

A handler runs for the gem in any impact slot (a ring's gem with "Sockets on other gear": `ctx.Slot` `Ring1` / `Ring2`,
`ctx.Carrier` the ring). When the mod unloads its handlers stop and the items stay until the server restarts. **A
removed mod's gem does nothing**: after a restart without the mod its gems are kiln.content's `[Missing: ...]`
placeholders. A trigger gem's placeholder runs its trigger program (Kiln serves it when no registered gem does), which
calls no handler; a program nothing serves (one the mod cooked itself) is none, so the game runs nothing for that gem.
It does not act as its base gem ([gems-new.md](gems-new.md), "Unload").

## Forge actions
Full reference: [gems-forge.md](gems-forge.md). A Shroud Forge's four actions from code, run as the game's own action (its
checks and its costs, paid by the player in the forge's material), and the events of the players' own forge actions.

| Call | The game's action |
|---|---|
| `Gems.Socket(player, carrier, gem, forge, options?)` | Insert: the gem into the carrier's empty socket |
| `Gems.Unsocket(player, carrier, forge, options?)` | Remove: the gem back into the player's backpack |
| `Gems.Upgrade(player, gemOrCarrier, forge, options?)` | Upgrade: +1 level, up to the gem's own cap and the forge's maximum |
| `Gems.Recycle(player, gemOrCarrier, forge, options?)` | Recycle: the gem destroyed for the forge's essence |

Each returns a `Task<GemCraftResult>` (`Ok`, `Status`, `Reason`, `LevelBefore` / `LevelAfter`, `Paid`, `Received`); a
refusal is a result, not an exception. `GemCraftOptions { Holder, Cost }`: a chest the player may use, `GemCost.Free` or
another price; since 0.2.0-dev.29.15 also `GemHolder` / `CarrierHolder` (the gem and the carrier in two places, one
transaction), `PayFrom` (a chest or the base's Magic chests pay) and `ReturnTo` (where an unsocketed gem and Recycle's
essence go), and `Gems.OfferChestGems` with `GemForgeOpenedEvent`: a container's gems in the forge's own GEMS list on Kiln
clients. A carrier is any item with a socket: a weapon, or a ring, armour piece or shield with "Sockets on other
gear" (lab: a ring by code, socketed and upgraded at a spawned forge). `GemCraftingEvent` (cancelable) comes before a
player's own action, `GemCraftedEvent` after any action that happened ("Which event for what" above).

Seen in a real game: chest gems in the GEMS grid and chests paying for an upgrade (bisdat, 2026-11-17, a world hosted on
a Linux PC under Proton; [gems-forge.md](gems-forge.md#seen-in-a-real-game-020-dev2915) says what passed and what is
not seen yet).

## Sockets on other gear
Full reference: [gems-sockets.md](gems-sockets.md). The game runs the gem of the item in any of the 11 impact slots, and
its save keeps a socket on any item; only the creation of a socket, the forge's lists and the vanilla forge's Remove are
weapons-only. Nothing changes until a mod asks:

- `Gems.AllowSockets(GemSocketKinds.Rings | GemSocketKinds.Armour, rollChance)` (both sides, while the mod is loaded):
  on the server `Gear.GemSlot` and `AddSocket` work on these kinds and new items of them roll an empty socket with that
  chance; on a Kiln client the forge's WEAPONS list and GEMS rows also show such gear that has a socket (not worn: take
  it off first).
- `Gear.GemSlot` on a give (Kiln.Items), or `"gemSlotOverride": true` on an item of a data pack: a new item with an empty
  socket.
- `Gems.AddSocket(item)`: an existing item re-created with a socket in the same slot (one transaction), keeping level,
  rarity, unlocked upgrades, dye and durability. Kiln reads the new item back: `Done`, or `Incomplete` with
  `GemSocketResult.Missing` naming what the new item lacks (the item is re-created either way).
- `Gems.ReturnSocketedGems(player)`: the uninstall path. Every gem in the player's non-weapon gear (worn included) back
  into their inventory, free, without a forge (`gems.craft`'s forgeless return). Run it before a server drops a mod that
  put gems into other gear: the vanilla forge cannot take them out again.

Balance is the mod's: a gem reacts to every hit of its wearer, and up to 11 gems can run at once.

## The parts together
A ring mod that adds its own gem, from the first ring to the uninstall (the server side; the lab test
`mods/tests/gems-integration-test` runs these steps):

```csharp
public override void OnLoad()
{
    Gems.AllowSockets(GemSocketKinds.Rings);                       // both sides: server sockets, client forge lists
    _reaper = Gems.Register(new GemDefinition { Id = "ringmod:reaper", BaseOn = "T1_Gem_GemOfBleeding",
        Trigger = GemTrigger.OnKill(), OnTrigger = ctx => Reward(ctx.Player, ctx.Level) });
}

async Task Craft(EntityId player, EntityId forge)
{
    var give = await Items.Give(player, ringId, 1, new Gear(20) { GemSlot = true });   // a ring with an empty socket
    var ring = Items.Read(player)!.Slots.First(s => s.ItemEntity == give.Changes[0].ItemEntity);
    var gem = Items.Read(player)!.Slots.First(s => s.Item == _reaper.Item);
    var r = await Gems.Socket(player, ring.Ref, gem.Ref, forge);  // the forge's price; GemCraftedEvent follows
    // worn, the ring runs the gem: Gems.Equipped(player) lists it in Ring1 or Ring2, a kill calls Reward
}

async Task Uninstall(EntityId player) =>
    Log.Info(string.Join(", ", await Gems.ReturnSocketedGems(player)));   // every gem out of rings and armour
```

| Member | Thread | Side |
|---|---|---|
| `Gems.Catalog`, `Get` | tick | server, a client with a world |
| `Gems.Equipped`, `Socket(item)` | tick | server (and a hosting client) |
| `Gems.Register` | tick (`OnLoad`) | every side that has the mod; handlers run on the server |
| `Gems.Socket(player, ...)`, `Unsocket`, `Upgrade`, `Recycle` | tick | server (and a hosting client) |
| `Gems.AllowSockets` | tick (`OnLoad`) | both: server part and client part |
| `Gems.SocketKinds` | any | both |
| `Gems.AddSocket`, `Gems.ReturnSocketedGems` | tick | server (and a hosting client) |
| the five events | posted on the tick thread | server (and a hosting client) |

The tasks complete on the tick thread or, for `ReturnSocketedGems`, on a worker thread; an `await` in mod code always
continues on the tick thread (the mod's `SynchronizationContext`).

## Capability rows
| Row | Kind | What it says |
|---|---|---|
| `gems.read` | managed, session (Kiln.Abilities, at its start and about once a second) | the read API and `GemSocketChangedEvent`: needs kiln.items (`service-missing`) with item instances (`items.instances`, its reason when off), kiln.types and the game's reflection of `Equipment.equipmentImpacts[].weaponGemImpactEntityId` (`symbol-missing` on a build without it) and kiln.world. Without kiln.gamedata it is available with the note that the catalog has no programs or configs. It does not wait for a server world: without one (the main menu, a joined client) it still reads available, `Socket` throws `KilnUnavailableException` (`gems.read`), `Equipped` lists no gem and no `GemSocketChangedEvent` is posted |
| `gems.triggers` | managed, session (with `gems.read`) | `GemTriggeredEvent` and `Gems.Register`'s handlers: needs `gems.read`, kiln.abilities and its native events (`abilities.natives`). `Gems.Register` also needs kiln.content's new items and, for a trigger, kiln.assets: it checks those itself and throws `KilnUnavailableException` naming `gems.triggers` (the row can read available without them) |
| `gems.craft` | sigdb feature (`server.d/gems-forge.json`; a hosting client composes it) | the forge actions and `GemCraftingEvent` / `GemCraftedEvent`, and `Gems.ReturnSocketedGems`' forgeless return. kiln-items.dll lowers it when the gem handler's layout does not check out (`symbol-missing`), without a usable kiln.world (`service-missing`), or after a fault (`faulted`, until restart); Kiln.Abilities lowers it where kiln.gemcraft is not here. Off on a client that joined another world |
| `gems.sockets` | sigdb feature, one per side (`server.d/gems-sockets.json`: the createPide hook; `client.d/gems-sockets.json`: the forge lists) | server: `Gems.AllowSockets`' server part, `Gear.GemSlot` on other gear, `AddSocket`, rolls, `gemSlotOverride` on any gear; client: the widened forge lists. Kiln.Abilities lowers it when neither kiln.items v7 nor kiln.ui.values' `gemforge_sockets` is here |
| `gems.payers` | sigdb feature (`server.d/gems-forge.json`, since 0.2.0-dev.29.15) | `PayFrom` and `ReturnTo` other than the player ([gems-forge.md](gems-forge.md#who-pays-and-where-things-go-since-020-dev2915)) |
| `gems.sources` | managed, session (since 0.2.0-dev.29.15) | offered chest gems at the forge: `Gems.OfferChestGems`, `GemForgeOpenedEvent` ([gems-forge.md](gems-forge.md#chest-gems-in-the-forges-own-gems-list-since-020-dev2915)) |
| `ui.gemforge.sources` | sigdb feature (client, since 0.2.0-dev.29.15) | the offered chest gems in the GEMS grid ([../ui-gemforge.md](../ui-gemforge.md#chest-gems-in-the-gems-grid)) |

## What is checked
Each part's page lists its own tests and labs ([gems-new.md](gems-new.md#tests), [gems-forge.md](gems-forge.md),
[gems-sockets.md](gems-sockets.md#tests)). Together:
- `managed/tests/Kiln.Blocks.Tests/GemsIntegrationTests.cs`: `Equipped` over all 11 impact slots, `GemTriggeredEvent`
  from each slot, a registered trigger gem's handler from each slot with that slot's carrier and level (G2's dispatch on
  G1's own `Equipped`), a placeholder gem running another mod's trigger program calls nothing, and which trigger
  programs Kiln serves for placeholders (`GemPlaceholderProgramsTests`).
- `native/services/content/tests/refs_test.cpp` (`placeholder_programs`): a placeholder's program reference nothing
  serves is none; one that resolves is kept; other unresolved references keep the base's value.
- `managed/tests/Kiln.Contract.Tests/GemSocketsTests.cs`: `AddSocket`'s read-back (`Done`, `Incomplete` and what is
  missing).
- Lab `mods/tests/gems-integration-test` (Wine dedicated server 26430253, a spawned 1_Player stand-in and a spawned
  Grassland forge, 2026-11-16). First start 27 PASS, 0 FAIL: the four rows on; `AllowSockets(Rings)`; `AddSocket` read
  back `Done`, nothing missing; a `Gems.Register` kill gem socketed into a ring with `Gems.Socket` at the forge (50
  essence paid, one `GemCraftedEvent`), upgraded 7 to 8 (91 essence), `GemSocketChangedEvent` `Socketed` then
  `LevelChanged`; the ring worn: `Gems.Equipped` lists it in `Ring1`; a killing blow of the wearer runs the gem's handler
  once with slot `Ring1`, the ring as carrier, level 8, and no `GemTriggeredEvent` for that gem; `ReturnSocketedGems`
  takes the gem out of the worn ring (the same gem entity at level 8, nothing paid, `GemCraftedEvent` `Unsocket` with no
  forge, `GemSocketChangedEvent` `Unsocketed`). Second start without registering the mod's backstab gem: its placeholder
  runs the backstab trigger program Kiln serves (not its base's Gem of Bleeding program), socketed into a ring and worn it
  runs and calls nothing, `ReturnSocketedGems` takes it back, the kill gem in the same worn ring runs (16 PASS, 0 FAIL; no
  "no valid Impcat Program" line in the game's log). Without Kiln's copy of the trigger program the placeholder had no
  program and spawned no gem impact (15 PASS, 0 FAIL; the game logged its error line about every step while it was worn).

The read API and events:
- Unit tests with a fake game (`GemsBaseTests`): the catalog and its cache, kiln.gamedata's JSON (programs, configs by
  alias and id, an alias two ids share), the Equipment layout against the reflection dump of server 26430253, `Equipped`
  and `Socket`, the impact to gem map (parent chain, one read per player and step, one walk per impact and step, a
  failed read of any kind costing only that player's events of the step), `GemTriggeredEvent`'s target, the
  native-event interest, the socket diff (socketed, unsocketed, upgraded, swapped, recycled, destroyed, moved away,
  moved to another socket, one gem back for two emptied sockets, first sight), the poll's turns (players looked for
  every 50 steps also when there are none, gems told by their category without kiln.gamedata, a player who could not
  be read or a new world seen afresh, no server world) and the two rows.
- Lab (`mods/tests/gems-test`, Wine dedicated server 26430253, a spawned player-template entity, 2026-11-16: 34 PASS,
  0 FAIL): both rows available; the catalog from the live game data (28 gems, programs, configs with ids, kinds,
  values, scale factors, aliases); a dagger given with `Gear.GemSlot` and a Gem of Puncturing at level 5, the gem
  written into the dagger's `WeaponGemSlot` for the probe (the forge's own Slot action is not used), the dagger in hand:
  `Gems.Socket` and `Gems.Equipped` (slot `Held`, the gem impact entity, carrier, gem, level 5), `ImpactStartedEvent`
  on that entity, `GemTriggeredEvent` with `applyBuff` on a Vukah after a pierce hit by the wearer, `GemSocketChangedEvent`
  (`Socketed`, `LevelChanged` while the probe's three field writes landed, `Removed` when the socket was emptied with
  the gem still loose), and a data-pack style change of `0x5e34f09b` (`mul 2`): the running impact kept 5, the catalog
  showed 6 within 2 s, and after switching the hotbar away and back a new gem impact ran with 8.

## Real-client checks
What only a real game client shows, in the order of one play session. Setup: a server and a Kiln client built from the
same tree; on both, GemSample, GemEffects and a mod with side `both` that calls
`Gems.AllowSockets(GemSocketKinds.Rings | GemSocketKinds.Armour)` in `OnLoad` and logs `GemCraftingEvent`,
`GemCraftedEvent`, `GemSocketChangedEvent` and `GemTriggeredEvent` (it may cancel a Recycle on request); a ring with a
socket and one without in the backpack.

1. Boot: `gems.read`, `gems.triggers`, `gems.craft` (server), `gems.sockets` available; client kiln.log `ui.values:
   features resolved: ... gems.sockets ...`, `gems.sockets hooks installed`, `Gem Forge lists widened to category mask 0x6`.
2. The new gems (Deep Cuts, Frostbite, Renewal): names, PNG icons, rarities, descriptions, German texts with the game in
   German; a new gem's tooltip shows its level. `/gemeffects 10` gives both GemEffects gems at level 10.
3. The forge's GEMS grid lists the new gems. Insert the Gem of Frostbite into a weapon by hand while the mod listens to
   `GemCraftingEvent`: the action goes through about a step later; one `GemCraftingEvent` (with the cost), then
   `GemCraftedEvent` (not by code, the cost paid) and later `GemSocketChangedEvent` `Socketed`. Upgrade it: the level
   rises (`LevelChanged`).
4. Frostbite in hand: a critical hit on an enemy slows it (snail icon). Renewal: a kill heals 10 at level 10. Deep Cuts:
   a critical cutting hit bleeds 9 s.
5. Cancel a Recycle (the mod cancels it): nothing paid, the gem stays; the forge screen shows it as it is at the next
   inventory update.
6. A code action (`Gems.Upgrade` from a command) while the forge screen is open: its lists refresh; the forge plays its
   action.
7. WEAPONS lists the socketed ring from the backpack or an action bar, not the ring without a socket, not a worn ring.
   Insert a gem into the ring: the price is paid, the gem leaves the backpack.
8. GEMS shows the ring as a carrier row with the gem's icon and level; `Ui.Values.GemForgeCandidates()` stays Ok with that
   row. Upgrade and Recycle the ring's gem there, Remove it, Insert again: each as for a weapon.
9. Tooltip of the socketed ring (with a gem, with an empty socket) in the backpack and worn: the socket row shows as on a
   weapon; the delete confirmation warns about the gem.
10. Wear the ring with a Gem of Bleeding: crits bleed with any weapon; the server's `Gems.Equipped` lists `Ring1` or
    `Ring2`. With the Frostbite in the ring instead: the crit slow works with the ring as the carrier.
11. `GemTriggeredEvent` for a spawning gem (Fracturing, Necromancy): the event comes with the spawned entity (the
    impact's parent chain).
12. A data-pack change of a gem's number on a Kiln client: the tooltip shows the new number after a hotbar switch.
13. `Gems.Catalog` on the client (a world loaded) lists the gems with programs and configs.
14. Quit to the menu and join again: the ring still holds its gem.
15. `Gems.ReturnSocketedGems` from a command with the ring worn: the gem is back in the backpack (the client's inventory
    shows it), `GemCraftedEvent` (`Unsocket`, by code, no forge).
16. Unload the socket mod (or `AllowSockets(None)` on the client): the next forge open shows vanilla lists.
17. A client without Kiln on the same server: the ring's gem works when worn, its forge lists are vanilla, the tooltip
    shows the socket; a new gem from a `clients: mod` mod shows "[Invalid Item Stack]".
18. Remove GemEffects from the server and the client, restart both: the socketed Frostbite shows as
    `[Missing: gemeffects:...]`; in hand it does nothing (no slow, and no bleed of its base Gem of Bleeding).
19. A hosting client (the player's own world): steps 3, 5 and 15 there (the hosted copy of `gems.craft`'s signatures was
    only checked offline).
