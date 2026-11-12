# Forge parity

What Minecraft Forge (and NeoForge) gives modders, and the Kiln equivalent. Status: **done** (verified in a lab or
live), **partial**, **client** (built, needs a joined player or the running client to confirm), **planned**.
Details per area: `docs/kiln/modding/` (loader, MDK, testing), `docs/kiln/{events,config,commands,permissions}.md`,
`docs/kiln/services/*.md`, `docs/kiln/{hooks,typegen,client,ui,network}.md`.

## Loading and tooling (FML, MDK)
| Forge | Kiln | Status |
|---|---|---|
| `mods.toml` metadata | `kiln.mod.json` v2 + `schema/kiln.mod.schema.json` | done |
| Dependencies (mandatory/optional, version ranges, ordering) | `dependencies` (semver `^ ~ =`, comparators, `\|\|`, Maven `[a,b)`), `loadBefore/After` | done |
| Incompatible mods | `incompatible` with a range | done |
| `loaderVersion` / forge dependency | `apiVersion` (Kiln.Api 0.2.0) | done |
| `Dist` (client/dedicated server) | manifest `side` server/client/both + `Mods.Side`, `KilnApi.side()` | done (client run passed) |
| Mod sorting + dependency error screen | resolver: order, cycles, missing/mismatched/incompatible reported, others load | done |
| ModList | `Mods.All/IsLoaded/Get/Current`, `ModEntry` with stats; dashboard `mods` | done |
| InterModComms | `Mods.Send` + `Context.Messages.On/Take` | done |
| Shared API jars | `exports` assemblies, dependents reloaded with them | done |
| FMLLoadCompleteEvent, ServerStarting/Started/Stopping | `ModsLoadedEvent`, `ServerStartingEvent`, `ServerStartedEvent`, `ServerStoppingEvent` (SIGINT verified), per hosted session on a client | done |
| Hot swap | hot reload with dependents, 100× leak-free | done (beyond Forge) |
| Crash reports with mod list | `kiln/crash-reports/` for managed and native faults; dashboard `crash_reports` | done |
| Faulty mod handling | fault limit per window → disable + `ModDisabledEvent` | done |
| MDK + ForgeGradle | `Kiln.Sdk`, Kiln.Api ref package, local feed, `KilnDeploy` (`-p:KilnReload=true`), `KilnPack` | done |
| MDK template | `dotnet new kiln-mod [--tests]`, `kiln-service` | done |
| GameTest | `Kiln.Testing` (`[KilnTest]`, ticks/until/delay, JSON reports, `test <mod>`, admin op `mod_test`) | done |
| JarJar | NuGet dependencies next to the mod, in its own load context | partial |
| Update checker | — | planned |
| Loader version resilience (FML remapping across game updates) | `sigport port` + `kilnctl update/port/gate` (blind replay 1273043 → 1285601: 125/125 symbols, 314/314 constants) | done (lab); live Steam path: manual |

## Mod API backbone
| Forge | Kiln | Status |
|---|---|---|
| `EVENT_BUS`, `@SubscribeEvent`, priorities, `@Cancelable`, `Event.Result` | `Events.On/Register/Post/Enqueue/HasSubscribers`, `[Subscribe]`, `EventPriority` (+Monitor), `CancelableEvent`, `ResultEvent` | done |
| `ForgeConfigSpec` (comments, ranges, world restart, COMMON/SERVER/CLIENT) | `Config.Register<T>`, `[Comment]/[Range]/[RequiresRestart]`, `ConfigKind`, commented JSONC, file watch, `ConfigChangedEvent` | done |
| Config GUI | `Config.Schema/Find`, `ConfigFile.TrySet`; dashboard `config_*` | done (in-game menu: client) |
| Config sync server → client | `[Synced]`, `SyncedValuesJson`, `ConfigSync` over `kiln:config` (see Client) | done (real client: joined a lab server, client 26404966) |
| Brigadier commands + argument types | `Commands.Literal/Argument`, `[Command]` methods, `Arg.*` (ints, enums, players, `~` positions, items, durations, time of day), `/help`, completion | done |
| Selectors | `@s @a @p @r @e[type,name,distance,x,y,z,limit,sort]`, `Arg.Entities`, `Selectors` | done (@p/@r with real players: client) |
| PermissionAPI (+ LuckPerms-style groups) | `Permissions.Register/Has/Explain`, groups mapped from server userGroups, `kiln/config/permissions.json`, hot reload; dashboard `permissions_*` | done |
| Server console | `kiln/control/console/*.cmd`, `kilnctl cmd` | done |
| `server.execute`, tick events | `Scheduler.Post`, `await NextTick()`, `Tick.Pre/Post` | done |
| Logging | per-mod logger, game log mirror | done |

## Gameplay events
| Forge | Kiln | Status |
|---|---|---|
| PlayerLoggedIn/Out | `PlayerJoinedEvent` / `PlayerLeftEvent` | done (simulated players) |
| ServerChatEvent | `ChatMessageEvent` (cancel, rewrite) | done; rewrite: client |
| CommandEvent | `CommandExecutingEvent` / `CommandExecutedEvent` | done |
| Time/sleep events | `TimeSetEvent`, `DayStartedEvent`, `NightStartedEvent` | partial |
| SettingChanged (no Forge equivalent) | `SettingChangedEvent` | done |
| LivingAttack/Hurt/Damage | `EntityHurtEvent` (Amount, cancel, lethal), `EntityDamagedEvent` | done (Kiln hits); real hits: client |
| LivingDeathEvent | `EntityDeathEvent` (victim, killer, source, origin), observe-only | done |
| LivingHealEvent | `EntityHealEvent` + `Cause` (hit, Kiln, regeneration, attribute heals) | partial (regeneration/potions: client) |
| LivingDropsEvent | `EntityDropsEvent` (cancel removes the loot container) | partial (game loot: client) |
| PlayerRespawnEvent | `PlayerRespawnEvent` | client |
| `getHealth/setHealth/heal/hurt/kill`, invulnerable | `Combat.GetHealth/SetHealth/Heal/Damage/Kill/SetInvulnerable`; dashboard `health/heal/god/kill/damage` | done |
| LivingFallEvent | `EntityFallEvent` (Damage writable, cancel) | done (game's fall-damage path); real falls: client |
| LivingKnockBackEvent | `EntityKnockbackEvent` (Strength/Direction/Reaction, cancel) | done |
| ItemCraftedEvent | `CraftEvent` (cancel) + `ItemCraftedEvent` | done |
| PlayerInteractEvent | `InteractEvent` (cancel) + `InteractionResultEvent` | done (injected); real players: client |
| LivingEntityUseItemEvent, player tick state (`isSprinting`, `isSwimming`, `isFallFlying` ...) | `PlayerConsumeEvent` (cancel), `PlayerActionEvent` (cancel), `PlayerClientActionEvent` (cancel), `player.Is(...)`, `PlayerStateChangedEvent` | lab (spawned player character); real players: manual test |
| EntityItemPickupEvent | `LootEvent` (cancel) + `ItemPickupEvent` | done |
| ItemTossEvent | `ItemTossEvent` (inventory Destroy/Trash, cancel) | client |
| BlockEvent.Break/EntityPlace, terrain | `BuildEvent` (observe) + `Gameplay.Protect` zones refusing build/break/terraform | client |
| LivingSpawnEvent.CheckSpawn | `NaturalSpawnEvent` (cancel) + spawn zones | client |
| EntityTeleportEvent | `TeleportEvent` (cancel/redirect) + `TeleportedEvent` | done |
| EntityJoin/LeaveLevel | `EntityJoinLevelEvent` (cancel) / `EntityLeaveLevelEvent` for every entity, `Gameplay.WatchEntities(categories)` | done |
| Land claims (mod ecosystem) | `mods/samples/Claims`, dashboard `zones_*` | done (zones), refusal: client |

## World data and content
| Forge | Kiln | Status |
|---|---|---|
| LevelEvent.Save / Load | `WorldSavingEvent`, `WorldSavedEvent`, `WorldLoadedEvent` | done |
| `/save-all` | `Saves.Request()`; dashboard `save_now` | done |
| SavedData | `WorldData.Get<T>`, consistent with the world file (kill -9 verified) | done |
| Player persistent data | `PlayerData.Get<T>(steamId)` | done (offline) |
| Capabilities / attachments | `EntityData.Get<T>(entity)` across restarts | partial (props; creatures: client) |
| Backups | `Saves.Backup/RestoreOnNextStart`, `kilnctl backup`; dashboard `backup_*` | done |
| Registries | `Content.Items/Recipes/Loot/Drops` (4,213 items, 2,542 recipes; fields from the game's reflection) | done |
| Data packs (recipes, loot) + `/reload` | `<mod>/content/*.json`, applied at world load in mod order, reverted with the mod, file watch | done |
| Global loot modifiers | `drops`/`loot` patches | done |
| Registry dump / data generators | `Content.Dump()` | done |
| Registry sync to clients | `ContentSync` over `kiln:content` (server's effective changes applied on join, reverted on leave) | done (real client) |
| DeferredRegister: new items | `Content.Items.Register(ItemDefinition)`, packs `items.add`, id = FNV-1a(GUID) | done (server + client, hosted and joined) |
| DeferredRegister: new recipes | `Content.Recipes.Register(RecipeDefinition)`, packs `recipes.add`, listed in the crafting menu | done (menu tab heuristic) |
| Missing mappings | removed mod's items stay until restart; recipes unlisted | done |
| IItemHandler | `Items.Give/Take/Read` on any inventory, gear | done |
| Entity spawning, teleport, game rules, time | `Spawn.*`, `Teleport.*`, `Settings.*` (38 live), `Time.*` | done |

## Server admin (beyond Forge)
| Feature | Kiln.Admin ops | Status |
|---|---|---|
| Mods page (reload/unload/load, tests, crash reports) | `mods`, `mod_*`, `crash_reports` | done |
| Config editor, permissions editor, console with completion | `config_*`, `permissions_*`, `command_complete`, `command_run` | done |
| Protection zones on the map, combat, content editor, Kiln backups | `zones_*`, `health/heal/god/kill`, `content_*`, `save_*`/`backup_*` | done (in-game effects: client) |

## Low level
| Forge | Kiln | Status |
|---|---|---|
| Mixins, coremods, access transformers | `Hooks.Detour/Mid` (callable original, priority chains), `Memory.Patch` (auto-restore), `advanced` mods only | done |
| Mappings (MCP/official/Parchment) | sigdb symbols verified per build + mod-shipped `sigdb/<side>.json` + `Kiln.Game` typegen from the game's reflection | done |
| Reflection helpers | `Symbols.Get/Find/Hole`, `Constants.Get`, typed component reads (`Kiln.Ecs`, generated per game build; `Kiln.GameTypes.Components` is obsolete) | done |

Known limit: under Wine, hot-reloading one of two mods that detour the same very hot function can stall the unload;
one mod per hot function (docs/kiln/hooks.md).

## Client
| Forge | Kiln | Status |
|---|---|---|
| Client-side loading | Kiln boots in `enshrouded.exe` (side client, own sigdb, verified build record) | done (3 runs) |
| Integrated server (single player / host) | server services in the hosted world (60 Hz), per-session lifecycle, services reset between sessions | done |
| `ClientTickEvent`, `Minecraft.execute`, `Minecraft.player` | `Client.Tick/Post/NextTick`, `Client.LocalPlayer`, `Client.State` | done |
| SimpleChannel networking | `Network.Channel(name, v).Register<T>`, `SendToServer/SendTo/SendToAll/SendNear`, over the game session (channel 12, no extra port) | done (hosted + joined lab, 26 ms ping) |
| FML handshake, mod list sync | HELLO/WELCOME, `NetClientHandshakeEvent`, `network.json` modSync off/warn/refuse | done (real client) |
| Mod mismatch rejection | modSync refuse → toast with the missing mods + game login failure | done (real client) |
| Vanilla clients/servers | `NetVanillaClientEvent`, `requireKiln`; vanilla peers never get Kiln frames | client (lab with simulated vanilla peers; a refusal with a real vanilla client: not run) |
| KeyMappings + Controls screen | `KeyBindings.Register/Rebind/Reset`, Keys tab, `kiln/config/keybinds.json` | done (real client) |
| Screens / GUI | `Ui.Window` (Dear ImGui over the game's Vulkan swapchain), keyboard capture | done; in-game mouse with a real mouse: to check |
| HUD overlays, toasts | `Hud.Show`, `Hud.Toast`, `Ui.Frame` | done |
| Mods screen, config screens | Kiln menu (Insert): Mods / Network / Keys / Overlay tabs, generated config page per mod | done (real client) |
| Config sync server → client | `[Synced]`, `ConfigSync` over `kiln:config`, read-only while connected | done (real client: joined a lab server, client 26404966) |
| Registry/content sync | `ContentSync` (see World data and content) | done (real client) |
| Sounds (SoundEvent, PlaySoundEvent) | `Kiln.Audio` `Sound`, soundcook | see the rows below |
| Particles (`level.addParticle`, ParticleEngine) | `Vfx.PlayAt/On/For`, any game VFX by name (`Kiln.Vfx`, relay `kiln:vfx`), a guard against unknown GUIDs | lab; paths and guards ran in a client; what shows: manual test |
| Particles (ParticleType, ParticleProvider, particle JSON) | `VfxBlock`, `ParticleBlock` (`blocks/*.json` or C#) | client (built; labs, CPU harness and real GPUs; not seen in a game) |
| Shader packs, custom render types, core shader overrides | `MaterialBlock` (new surfaces on the game's shaders), `ParticleBlock.Shader` (a mod's own particle hooks in HLSL) | client: materials and particle shaders built, run on real GPUs outside the game, not seen in a game; transparent and opaque surface shaders: researched, planned |
| Post chains (`PostChain`, `GameRenderer` effects) | `ScreenEffectBlock` (colour grading, tint, vignette, fades, flashes), `ScreenEffects.Play` | client (built; tested headless on real GPUs; not seen in a game) |
| Render events (`RenderLevelStageEvent`) | `RenderFrameEvent`: drawing on the screen and at world positions with the game's camera | client (built, unit-tested; not run in a client) |
| Title/pause screen buttons (`ScreenEvent.Init`) | `Ui.Menus.Add` (drawn by the game's own menu code) | done (real client) |
| Custom screens, container screens, recipe book tabs | `Ui.Screen`, `Ui.Crafting.AddPanel/AddTab/AddSubcategory` | done (real client) |

## Launcher, other mods, new content, abilities, creatures
| Forge / ecosystem | Kiln | Status |
|---|---|---|
| Launcher with instances (CurseForge/Prism) | Kiln Launcher (Tauri): install/upgrade/uninstall, profiles, library, configs, logs, saves, save editor | done (real client) |
| Server packs | modpack build/publish, refusal → consent → install → rejoin | done (real client) |
| Server admin panel | launcher admin pages over KAP/1 (TCP + token), file bridge kept | done (lab) |
| Coremods / early loading, other loaders' mods | `kiln/native` (dllmain/early/boot/world phases), `chain.ini`, EMF compatibility (EmfApi v21) | done (real client) |
| Resource packs, custom icons/textures/models | `kiln.assets` (+ `tools/kfc`, `tools/modelcook`) | done (real client) |
| Lang files | per-language `texts` | done (log; visual: to check) |
| Missing registry entries | placeholders kept per world | done (lab) |
| Loot table / trade injection | `loot`, `drops`, trader recipes | done (lab) |
| Block access, structures, WorldEdit | `kiln.build` (`Build.*`, `.kschem`, `PlaceOnce`, undo) | done (vanilla client sees it) |
| Entity attributes, AI tuning, variants | `kiln.creatures` + gamedata `templates`/`arsenals` | done (vanilla client sees scale/level/faction) |
| Data-pack game data (abilities, buffs, perks, skills) | gamedata sections + clones | done (lab; tooltip synced on client) |
| Keybind abilities, explosion/projectile events, effects | `kiln.abilities` (`Abilities.Register`, `ImpactStartingEvent`, `ApplyBuff`, C# natives) | done (real client) |
| Datapack functions / scripting | Impact programs (`tools/impact`) + Kiln natives | done (lab + client host) |
| Server mods in an integrated server | side "server" mods load with each hosted world, unload when it ends | done (real client) |
| `LivingEntity.isDeadOrDying` | `Combat.GetHealth().Dead` (the game's death check) | done (real client) |
| Custom potion effects / enchantments | `buffs.add` / `perks.add` clones, `Abilities.ApplyBuff`, perk slots; placeholders | done (lab + client tooltip) |
| Item models with matching icons | assets.json `icon` (game and new items), `tools/assets/render-icon.py` | done (real client) |
| Entity AI follow/guard | `Command.Guard(target, GuardOptions)`, `Creatures.GuardOrders` | done (lab; client walk + catch-up) |
| Play an animation on a player | `Abilities.PlaySequence(player, guid, SequenceOptions)` | done (vanilla client) |
| Player XP, levels, skill points, skill unlocks | `Player.Progression`, `PlayerLevelChangedEvent`, `/level /xp /skillpoints /skill` | done (vanilla client) |
| Sounds (SoundEvent registry / sound resources) | `tools/soundcook` cook.json sound/cue/voiceover steps + kiln.assets (playback: Kiln.Audio) | done (real client: heard in the round 2 human test) |
| Sounds playback (playSound, music, voice) | `Kiln.Audio` Sound/Voiceover/SoundId, `kiln:audio` relay | done (real client: menu sounds and the sound tour of the round 2 human test) |
| (beyond Forge) Nodes in the game's skill tree | `skills/*.json` `gameTree`, `GameTree`, `kiln.skills` | lab; drawing and unlocking seen in the client |
| (beyond Forge) New talent trees and screen | `Skills.Register`, `TalentTree`, channel `kiln:skills` | lab; screen seen in the client, actions: manual test |
| Cancelable events pattern | `TalentUnlockingEvent`, `TalentChangedEvent` | lab |
| mods.toml / metadata validation at load | `kiln lint` (`kiln_manage::lint`), VS Code extension, launcher `library_lint` | done (lab, tests) |
| Advancements: award, revoke, read, event | `Knowledge.Grant/Revoke/Get/Query`, `Journal.State`, `KnowledgeChangedEvent` | world: lab; player: manual test |
| Villager or NPC interaction events | `DialogueLineEvent`, `DialogueKnowledgeEvent` | manual test |
| Server-side validation of client actions | `vetoClientKnowledge`, `checkDialogue`, `KnowledgeRequestEvent` | manual test |
| Debugger attach to a running server | `kiln debug --dap` (netcoredbg) | dedicated servers on Linux/Wine; Windows and client not covered |
| MDK templates | Kiln.Templates: 24 kinds, including native, a Kiln service and unit tests (`kiln-unit-test`) | lab |
| Native mod SDK | Kiln.Sdk `native/`, `kiln_plugin` | built from the package with no repo |
| Compile-time mod checks | Kiln.Analyzers KILN101-108, KILN110-112 (blocks), KILN190-198 (Kiln.GameIds: catalog, field paths) + code fixes | done |
| Game ids as constants | Kiln.GameIds + `kiln catalog` | done |
| Modder CLI | `kiln` (new, build, cook, deploy, pack, run, reload, test, debug, logs, lint, blocks, catalog, vfx, skeleton) | done |
| API docs site | `tools/docs-site` (DocFX) | builds locally, not published |
| Custom advancements / quest mods (FTB Quests) | `Quests.Define`, `quests/*.json` | engine: lab; players: manual test |
| Advancement triggers and rewards | objectives, `Quests.Signal`, `QuestInput`; rewards xp, item, knowledge, call | synthetic and world paths: lab; xp, items: manual test |
| Advancement events | `QuestStartedEvent`, `QuestStepEvent`, `QuestCompletedEvent`, `QuestFailedEvent`, `QuestStateChangedEvent`, `QuestObjectiveProgressEvent` | done |
| Advancements screen, toasts, NPC dialogue screens | the game's journal, notifications, NPC menu, by default; `"journal": "chat"` + `/talk` for vanilla clients | journal, menu, voice: seen in a client; notifications, HUD: manual test |
| SavedData, data-driven definitions + reload | `QuestBook` in Kiln.Saves; `quests/*.json`, `kiln lint`, `/questadmin reload` | done |
| Advancements and quests in the game's own UI | the default for `quests/*.json` and `Quests.Define`; `NativeQuests` | server: lab; journal entry: seen in a client; counters, markers: manual test |
| Quest objectives placed in the world | step `site`, `spawn`, `place`: voxel surface, outside bases, verified with retries | lab; in a client: manual test |
| Custom data-driven progression flags | `quests/*.json` `knowledge`, `Knowledge.Resolve("<mod>:<name>")` | world: lab; player: manual test |
| New villagers and NPC entity types, trades | `npcs.*.clone`, `roster`, `vendor` (shared workshop) | lab; looks and menu: manual test |
| Villager AI commands | `Villagers.GoTo/Follow/Stay/Teleport`, `VillagerStateEvent` | Teleport: lab; rest: manual test |
| NPC dialogue | dialogues with `voice`, any `if` | data: offline and lab; topics and voice: seen in a client |
| Player capabilities per character | `CharacterData`, `Player.CharacterId` | lab; real id: manual test |
| Data that follows the player between servers | `CharacterData.Carried`, channel `kiln:character` | server side: lab with fake peers; Kiln client: manual test |
| `Item.Properties.rarity`, fixed item stats | `ItemDefinition.Fields["rarity"]`, `["itemLevelRange.minLevel"]`, `RolledLoot`, `kiln lint` KL406 | lab; client check pending |
| KeyMapping context (no firing in text fields) | `KeyBinding` text-key rule, `OnGameScreens` | done (client check open) |
