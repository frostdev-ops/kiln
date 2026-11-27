# Where the answers are

The wiki: https://frostdev.io/kiln/wiki/<page>.

The generated API reference of the newest release: https://frostdev.io/kiln/api/. Each release keeps its own copy at
https://frostdev.io/kiln/downloads/<version>/sdk/api/ (a few older versions have none).
It covers every public type and member with its XML docs, so look there when this skill or the wiki doesn't name a call.
For a mod that builds on an older release, use that release's copy: the newest one can name members it lacks.

This skill's own guides to the C# API, by area ([api.md](api.md) is the index): [contract, threads and which assembly holds a
type](api-contract.md#which-assembly-holds-a-type-kilnservices), [events, config, commands, players](api-events.md),
[items and recipes](api-items.md), [gear and rarity](api-gear.md), [content, creatures, abilities](api-content.md),
[world, block queries and building tools](api-world.md), [UI](api-ui.md), [network and client](api-network.md),
[hooks and other engines](api-hooks.md).

| Question | Page |
|---|---|
| Start a project | `first-mod`, `dev-kit` |
| Make a spell, a status, an effect, an item or a weapon | `blocks`, `new-items` |
| Make a prop, a station that works on its own (a forge, a drying rack), comfort | `world-blocks` |
| A cosmetic, a hat, a cape: a model worn on the body | `blocks`, `models` |
| The `kiln` command | `cli` |
| Manifest, sides, `clients`, version ranges, boot content, update checks | `manifest` |
| Events, configs, commands, saved data, other mods | `code-mods`, `events` |
| Content the game reads at start, resource patches (dev.28+) | `onboot` |
| Heavy work off the game thread, task world reads (dev.29.18+) | `tasks`, `tasks-world` |
| Permission nodes and groups | `permissions` |
| Hot reload, the control folder | `hot-reload` |
| In-server tests | `testing` |
| Why a mod didn't load, faults, breakpoints, the game's impact memory | `debugging` |
| Linter and analyzers | `lint` |
| VS Code | `vscode` |
| Game ids | `catalogs` |
| Items, recipes, loot, drops | `data-packs` |
| New items | `new-items` |
| Move gear with its state; equip, equipment sets; quick-stack rules; chest names | `inventory` |
| Boot ECS declarations, client-world programs, wraps of the game's programs | `world-programs` |
| Production stations, bases, Magic chests (dev.29.14+) | `stations` |
| Gems and the Shroud Forge (dev.29.11+) | `gems` |
| Who a creature fights, how it sees, brains, creature control | `creature-ai` |
| Flame level, spires, bosses, world progress | `world-progress` |
| Spells, creatures, attacks as data; clones | `game-data` |
| One creature, variants, spawn rules, guard | `creatures` |
| Scripted abilities, per-cast tweaks, buffs | `abilities` |
| Impact programs | `impact` |
| Levels, XP, skill points | `progression` |
| New skill nodes, talent trees | `talents` |
| Quests, conversations | `quests` |
| Placed and new NPCs, the game's journal and menus | `npcs` |
| Sounds, music, voice | `sounds` |
| Particles, lights, the game's VFX: [vfx.md](vfx.md) | `blocks` (section VFX), `shaders` |
| Screen tints, flashes, fades | `screen-effects` |
| Drawing on screen and in the world | `drawing` |
| Icons, retextures, resources | `assets` |
| 3D models | `models` |
| New surfaces for models from PNGs and numbers | `blocks`, `shaders` |
| Terrain, props, schematics, zones, terrain overlays, world queries and paths | `world-edits` |
| Menus, screens, crafting tabs, HUD, keys, item icons, values in the game's UI | `game-ui`; data blocks: `ui-blocks` |
| Looks, themes and fonts (dev.29.16+) | `ui-looks` |
| NoClip, player rules, creative mode | `player-rules` |
| Mod texts in several languages, the game's own texts | `languages` |
| Network channels, synced configs, client code | `multiplayer`, `network` |
| A mod's own Steam peer-to-peer messages (dev.30.3+) | `steam-messages` |
| Building as the local player: select, place, remove, undo, dismantle (dev.30.3+) | `building-input` |
| Mods of other engines (EML, EMF, closed DLL mods), chain loading | `other-mods`, `emf-mods`, `eml-shroudforge`, `more-frameworks`, `dinput8-chain` |
| ShroudForge mods on Kiln's own interpreter (kiln mode, preview, dev.30.2) | `kiln-mode` |
| Hooks, patches, typed hooks (advanced tier) | `hooks` |
| Forge concepts | `forge` |
| Kiln's menus in the game, chat history, other mods' slash commands, graphics tools | `in-game` |
| What works today | `status` |

Core services and the mod lifecycle (pages in the repository's `docs/kiln/`):

| Question | Page |
|---|---|
| Lifecycle events (`ModsLoadedEvent`, `ServerStartedEvent`, `ModReloadedEvent`), the Mods API, IMC, hot reload | `modding/lifecycle.md` |
| What happens when a mod throws: the fault limit that disables it, crash reports | `modding/faults.md` |
| `kiln debug`: breakpoints, call stack and locals in a running local server | `modding/debugging.md` |
| World edits the game saves and replicates (terrain, building blocks, props), schematics, undo, `Build.PlaceOnce`, `/build` | `services/build.md` |
| Create and destroy entities from templates (creatures, item drops, props), the per-world list of what Kiln spawned | `services/spawn.md` |
| The server's game settings, live (enemy and boss factors, day length, weather, XP), written back on request | `services/settings.md` |
| One creature at runtime: catalog, per-creature values, variants, AI events, commands | `services/creatures.md` |
| A mod's own resources, overrides of the game's, item icons, retextures | `services/assets.md` |
| Play sounds, music, voice-over and narration from code (client) | `services/audio.md` |
| New nodes in the game's skill tree, Kiln talent trees | `services/skills.md` |
| Levels, experience, skill points from code | `services/progression.md` |
| The game's knowledge (quest steps, NPC rescues, dialogue heard): read, grant, revoke, watch; the vanilla journal; the client veto | `services/knowledge.md` |

dev.26+ (pages in the repository's `docs/kiln/`):

| Question | Page |
|---|---|
| What a mod cannot do yet, hard caps, the planned release | `modding/limits.md` |
| Error rule, `EntityRef`/`PlayerRef`, side and thread markers | `modding/contract.md` |
| What works on this build and why not (`Capabilities`, `capabilities.json`) | `services/caps.md` |
| Typed ECS reads (`Kiln.Ecs`), layout stamps | `typegen.md` §4, §6 |
| Unit tests with no game (`Kiln.TestKit`) | `modding/unit-tests.md` |
| Guided client checks (`[ClientCheck]`) | `modding/testing.md`; the pack: `client-pass.md` |
| The achievement guard (`Kiln.SteamGuard`) | `services/steamguard.md` |
| Persistent per-player bags, quivers and spellbooks (dev.30.7+, mod API 0.2.16) | `modding/player-storage.md`, `services/player-storage.md` |
| Data on one item (`InstanceData`) | `modding/inventory.md` "Data on one item"; `services/saves.md`, `services/items.md` |
| A mod's own player animation states (`assets/player-states.json`) | `modding/animations.md` "Player states"; `services/playeranim.md` |
| Weapon moves, animated held models, clips reworked at cook time | `modding/item-blocks.md` "Moves"; `modding/animations.md` "Clips reworked" |

dev.26+ samples: ItemData, PlayerStates, WeaponMoves.

dev.27+ (pages in the repository's `docs/kiln/`):

| Question | Page |
|---|---|
| Bonuses rolled onto one item (`Affixes`, affix blocks) | `modding/affixes.md` |
| What Kiln.Sdk bakes into a mod at build, and what not | `modding/baked.md` |
| Cook recipes, receipts, the `cooked` rule | `modding/cooking.md` |
| KILN120 (a result nobody reads), KILN121 (a capability nobody asked) | `modding/analyzers.md` |
| The one contract, results, change events | `modding/contract.md`; capability rows: `services/caps.md` |
| NoClip and player rules | `services/rules.md` |
| Taking a buff off, listing buffs | `services/abilities.md` |
| A player's aim | `services/player-state.md` |
| Kick, ban, the login event | `services/players.md`, `network.md` |
| Event filters, which events name entities | `events.md`, `services/combat.md` |
| Equip and unequip; item data in players' games, `Claim`/`Discard` | `services/items.md`, `modding/inventory.md`, `services/saves.md` |
| Weather | `services/time.md` |
| Tracked world writes, `"writes"`, `WrittenOverBy` | `services/world.md`, `modding/manifest.md` |
| Mouse key bindings, the scrolling pause menu | `ui.md` |
| The test kit's fakes (spawn, chat, items, combat, time, world) | `modding/unit-tests.md` |
| What a mod cannot do yet | `modding/limits.md` |

dev.27+ samples: SavedData, AdminTools, CalmBell, QuietServer, Loadouts, SkyControl, AffixSword, EngineRoom (advanced).
dev.27+ templates (`kiln new <name>` or `dotnet new kiln-<name>`): `events`, `saved-data`, `rules`, `network`,
`screen-effect`, and advanced `hooks`, `shader`, `world-program`.

dev.28+ and dev.29+ (pages in the repository's `docs/kiln/`):

| Question | Page |
|---|---|
| OnBoot, boot reads, resource patches | `modding/onboot.md`, `modding/boot-gamedata.md`, `modding/resource-patches.md` |
| Update checks, `kiln publish` | `modding/updates.md`, `modding/publishing.md` |
| Voxel rays, ground, line of sight, paths | `services/queries.md` |
| Terrain overlays | `services/terrain.md` |
| Creature control, NPC perception | `services/creature-control.md`, `modding/npc-blocks.md` |
| Pets (records, summon, follow, revive, the owner's bar) | `services/pets.md`, `modding/pets.md` |
| Taming (attempts, exact-cost steps, refund policies, the game's own taming) | `services/taming.md`, `modding/taming.md`, `services/item-costs.md` |
| Block kinds of your own | `modding/block-kinds.md` |
| Refusable events (death, pickup, equip) | `services/decisions.md` |
| Program wraps, `Systems.Define`, prediction storage | `services/systems.md`, `modding/ecs-programs.md` |
| Client-world programs | `services/world.md` "Client world programs" |
| Component watches | `services/decisions.md`, `events.md` |
| Typed hooks and `Invoke` | `services/hooks.md` |
| Same-step natives, `[ImpactNative]`, `EffectBlock.Run` | `modding/impact.md`, `services/abilities.md` |
| Equipment sets, tool wear | `services/items.md` |
| Material pools, world tables | `services/worldtables.md` |
| NPC roster past 64 | `services/npcs.md`, `services/quests.md` |
| NPC speech bubbles: Say, Suppress, Replace (`Kiln.Npcs`, dev.31.16+) | `services/npc-speech.md` |
| Gems | `services/gems.md`, `services/gems-new.md`, `services/gems-forge.md`, `services/gems-sockets.md`, `ui-gemforge.md` |
| Bases and stations | `services/stations.md` |
| Item icons and names, looks and themes, fonts | `ui.md` |
| The game's own texts (`Content.Text`) | `services/content.md` |
| Kiln tasks, `WorldConcurrent` | `modding/tasks.md`, `services/tasks.md`, `services/world.md` |
| Analyzers, retired KILNX003 | `modding/analyzers.md` |
| Unknown slash commands, `chat.json` | `services/chat.md` |
| Graphics status | `graphics.md` |
| EMF parity | `emf.md`, `emf-parity.md` |
| dev.29.19+: Kiln's language (`KilnLanguage`), presses as they come (`KeyInput`), XHL-Core's reflection reads | `ui.md` |
| dev.29.19+: hooks inside another mod's DLL (`Hooks.Module`); native `open_module`, `module_scan`, `read_original`, `code_state` | `hooks.md`, `services/hooks.md` |
| dev.29.19+: resident native tasks (`KILN_TASK_RESIDENT`) | `services/tasks.md` |
| dev.29.19+: the impact memory watch | `services/abilities.md`; wiki `debugging` |
| dev.29.19+: station screens with station outputs | `services/stations.md` |
| dev.29.19+: Enshrouded ModLoader's mods in Kiln (`dllmod`) | `engines/dllmod.md` |

dev.28+ and dev.29+ samples: boot-gallery, OreVein, TerrainSample, TransformScale, BlobCatalog, BlockKinds,
CreatureControlSample, CreatureAi, Decisions, WaterWorks, TypedHookVolume, FrostMark, equipment-sets, MaterialPools,
Budgets, ValueBars; dev.29.11+ GemSample, GemEffects; dev.29.18+ FetchNearby, SampleTasks.
dev.29+ templates: `system-wrap`, `client-program`.

dev.30+ (pages in the repository's `docs/kiln/`):

| Question | Page |
|---|---|
| Gear values in the tooltip's numbers: the `gear` and `gearRules` pack sections, `Content.Gear` | `services/content.md` "Gear values", "Gear rules" |
| Gear in code: rarity, enhancement slots, level (`Items.GearOf`), raising rarity (`Items.UpgradeRarity`) | `services/items.md` "Gear: rarity, enhancements, level", "Upgrading rarity" |
| Rarity at creation, rarity upgrades (`RarityRollBlock`, `RarityUpgradeBlock`) | `modding/item-blocks.md` "Rarity at creation", "Rarity upgrades" |
| Upgrade stations (`UpgradeStation`) | `modding/world-blocks.md` "Upgrade stations" |
| Lines and numbers in the game's item detail view (`Ui.Tooltip`), marks and clicks on the game's slots (`SlotMark`, `InventoryScreens`) | `ui.md` |
| dev.30.5+: building tools, brushes, material pools (`BuildingToolBlock`, `TerrainBrushBlock`, `ToolMaterialPoolBlock`) | `services/tools.md` |
| dev.30.5+: what a tool's primary and secondary action does (`TerrainToolAction`, add and remove modes, capabilities) | `services/tool-actions.md` |
| dev.30.5+: what placing terrain costs (pool items, normal building supplies, undo refunds) | `services/tool-payment.md` |
| dev.30.5+: terrain-only removal, a per-tool list of removable materials | `services/terrain-tool-carving.md` |
| dev.30.5+: a mod's item or recipe tracked as game knowledge (`KnowledgeBlock`) | `modding/item-blocks.md` "Tracking"; reads and changes: `services/knowledge.md` |
| dev.30.6+: block queries and controlled breaking (`Build.BlockAt`, `FindWall`, `BreakBox`) | `services/build-blocks.md` |
| dev.30.6+: player crafting from linked chests, dye sources (`Crafting.AddSource`, #125) | `services/stations.md` "Player crafting sources"; [api-items.md](api-items.md#crafting-from-linked-containers-306-mod-api-0215) |
| dev.30.6+: create and equip one stack in one step (`Items.GiveEquip`, #126) | `services/items.md` "Give straight into equipment"; [api-items.md](api-items.md#inventory-transfers-dev20) |
| dev.30.6+: mods' rows in the HUD's arrow and spell lists (`Ui.AmmoList`, #127; client) | `ui.md` "The HUD's ammunition and spell lists"; `services/ui.md` "kiln.ui.ammo"; [api-ui.md](api-ui.md#game-ui-hud-keys) |

dev.30+ samples: GearValues, RarityAnvil, SlotMarks.

Sample mods: Hello, SampleLib, SampleConsumer, SampleTests, BigStacks, NewItems, SpellTweaks, CreatureTweaks, EliteVukah, FireNova,
ImpactSample, AssetSample, ModelSample, SoundSample, VfxSample, SkillTreeSample, quests-sample, NpcSample, Schematics, MenuButtons, UiSample,
NetSample, Claims. dev.20+: UiBlocks, WorldPrograms and the block templates in [setup.md](setup.md).

Kiln's reverse-engineering notes are for its own developers. Mods never need them. Don't copy addresses or offsets into mod code.
