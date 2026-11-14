# Writing Kiln mods

Kiln loads C# mods into the Enshrouded server and client. Each feature page states what still needs a client check. This folder is the modder's guide; the
framework's internals are in `docs/kiln/architecture.md`. What is available on a build, and each guided client check's last verdict, is in the
generated [../status.generated.md](../status.generated.md); what has no API yet is in [limits.md](limits.md).

| Page | What |
|---|---|
| [mdk.md](mdk.md) | The MDK: `dotnet new kiln-mod`, Kiln.Sdk, build, deploy, pack, local NuGet feed |
| [baked.md](baked.md) | Definitions baked at build: quests, game-tree nodes, VFX and materials a mod defines in code, there at the first start |
| [manifest.md](manifest.md) | `kiln.mod.json` v2: fields, version ranges, dependencies, load order, exports, sides |
| [lifecycle.md](lifecycle.md) | Mod and server lifecycle events, the `Mods` API, inter-mod messages, hot reload |
| [testing.md](testing.md) | Kiln.Testing: `[KilnTest]` methods that run inside the server |
| [unit-tests.md](unit-tests.md) | Kiln.TestKit: a mod under dotnet test with no game, dotnet new kiln-unit-test, [KilnTest] methods as xunit tests, what is real offline |
| [faults.md](faults.md) | Fault policy, crash reports, `kiln.json` `mods` settings |
| [contract.md](contract.md) | The API contract: the one error rule, identity types (`EntityRef`, `PlayerRef`, `ItemId`), thread attributes |
| [../services/caps.md](../services/caps.md) | Asking what works: `Capabilities.Is/Get/Why/All`, why a capability is off; the manifest's `features` rules (manifest.md, "Asking what works") |
| [limits.md](limits.md) | What a mod cannot do yet: each gap, what stands in the way, the release it is planned for |
| [blocks.md](blocks.md) | Building blocks: effects, statuses, spells, abilities, perks, talents, bodies, loot, affixes and UI; what they compile to, client requirements and errors. Start here to make something |
| [affixes.md](affixes.md) | Affixes: rolled modifiers on one item ("+5 Strength", "+10% Move Speed"), as affix kinds and pools, which are blocks (`Blocks.Define`, `affixes` and `affixPools` in `blocks/*.json`, or `Affixes.Define`); kept with the item, applied while it is worn or in hand, shown in players' games (dev.27) |
| [inventory.md](inventory.md) | Moving items between inventories keeping their state (a stack, quick stack, move all), container names, transfer rules and events (ported from shroudlink), and why gear data lasts only a session |
| [item-blocks.md](item-blocks.md) | Items, weapons, wands, armor, stations, loot tables and recipes as blocks: no base items, no GUIDs; a weapon's own moves and held models with moving parts |
| [world-blocks.md](world-blocks.md) | Props, crafting props, stations, containers with default loot and lids, doors, colliders and building materials |
| [offers.md](offers.md) | Interaction offers from C#: the "E" prompt on any entity at run time, with a handler when a player accepts it (`Kiln.Offers`) |
| [npc-blocks.md](npc-blocks.md) | Creature bodies, animation, AI, behaviour, drops, friendly NPCs and vendors |
| [npc-ai.md](npc-ai.md) | Creature AI and behaviour: who a creature fights, how it notices, chases, wanders and runs, and the rules on top of the game's combat |
| [ui-blocks.md](ui-blocks.md) | Windows, HUD lines and key bindings as blocks, in C# or JSON: rows on your config and a few game values, buttons that run a command, set a value, cast an ability or open a window; server validation and the client checklist; `mods/samples/UiBlocks` |
| [cosmetics.md](cosmetics.md) | Pieces for the vanity slots on models of your own: skinned or on one joint, both bodies from one file, `kiln skeleton player` |
| [impact.md](impact.md) | Impact programs: tools/impact (dis/asm/lint/clone/cook), Kiln natives, packaging into kiln.assets |
| [ecs-programs.md](ecs-programs.md) | ECS programs in the game's scheduler (`kiln.programs.json`, `EcsPrograms.Bind`) and hooks on the game's systems |
| [sounds.md](sounds.md) | Sounds, cues, music, voice-over: tools/soundcook, cook.json sound steps, SoundSample |
| [vfx.md](vfx.md) | Effects: particles, lights and the game's VFX by name, in `blocks/*.json` or C#, the budget, VfxSample |
| [shaders.md](shaders.md) | Particle shaders (advanced): your own HLSL hooks for a particle, linked into Kiln's sprite; `kiln-vfxcook shader` |
| [screen-effects.md](screen-effects.md) | Full-screen effects: colour grading, tint, vignette, fades and flashes, on a status, an effect or from code |
| [models.md](models.md) | Weapons and creature bodies from glTF, rigid or skinned: tools/modelcook, cook.json model steps |
| [cooking.md](cooking.md) | Cooking: the files a mod makes from each player's game (models, animations, sounds, Impact programs), who cooks and when (the launcher, `kiln cook`, a server), and why only what this PC cooked is loaded |
| [materials.md](materials.md) | New surfaces for models from PNGs and numbers, on the game's own shaders: no base to name, or made from a glTF's materials |
| [animations.md](animations.md) | Clips, skeletons and state-machine graphs from glTF: tools/animcook, cook.json animation steps, clips reworked from other clips (`from`); states of a mod's own in the player's animation graph (`assets/player-states.json`) |
| [quests.md](quests.md) | Quest chains, conversations and NPCs: QuestBlock/DialogueBlock, quests/*.json, Quests.Define, rewards and client requirements |
| [texts.md](texts.md) | The game's English texts by loca tag (`Texts.Get`, `GetPlain`), also on a dedicated server from a local catalog |
| [lint.md](lint.md) | `kiln lint`: usage, output, JSON dialects, every rule with an example and the fix; [schemas/](schemas/) is the field reference of each format |
| [vscode.md](vscode.md) | The VS Code extension (tools/vscode): schemas, lint, game ids, build/deploy/reload, Testing view, kiln.log, KAP/1 console, Impact assembly |
| [cli.md](cli.md) | The `kiln` command: new, build, cook, deploy, run, reload, test, debug, lint, pack, publish, logs, blocks, catalog, vfx, skeleton |
| [publishing.md](publishing.md) | `kiln publish`: releasing a packed mod at GitHub (plan, apply, publish), the token, what a published zip may hold |
| [analyzers.md](analyzers.md) | KILN1xx: compiler warnings about threads, hot reload and the manifest, with code fixes |
| [catalogs.md](catalogs.md) | The local game-data catalog and the game-id constants |
| [../typegen.md](../typegen.md) | Typed reads of the game's ECS components: `Kiln.Ecs` (`TryGet`, `Get`, `Has`, `All`, `Supports`), generated per game build; example in §4.3 |
| [typed-fields.md](typed-fields.md) | The game's data as typed C# structures, for what blocks don't cover: `GameFields`, `GameEnums`, checked string paths |
| [player-states.md](player-states.md) | Every state and action a player can be in: `player.Is(...)`, `PlayerStateChangedEvent`, `PlayerActionEvent`, and what is left out |
| [../services/steamguard.md](../services/steamguard.md) | The achievement guard (experimental, game client only): keep tests and admin commands from unlocking Steam achievements |
| [debugging.md](debugging.md) | Breakpoints in a running server: `kiln debug` and netcoredbg |

## Five-minute start
```sh
dotnet new install Kiln.Templates --add-source <feed>       # once; <feed> = $KILN_OUT/nuget from managed/Kiln.Sdk/pack.sh
dotnet new kiln-mod -n MyMod --feed <feed> --server ~/srv/enshrouded/labs/<lab> [--tests] [--services "Players;Chat"]
cd MyMod
dotnet build -t:KilnDeploy                                  # copies the mod into <server>/kiln/mods/my-mod
dotnet build -t:KilnDeploy -p:KilnReload=true               # ...and hot-reloads it in the running server
dotnet build -t:KilnPack                                    # bin/<config>/my-mod-0.1.0.zip
```
`kiln/logs/kiln.log` shows `mod my-mod 0.1.0 loaded in … ms`; `echo mods > kiln/control/m.cmd` lists every mod with
its state (`kiln/control/m.result.json`).

## Samples (`mods/samples/`)
Each folder is a mod project: `dotnet build <folder>/<name>.csproj` (where a sample has a `build-assets.sh`, run it
first). `mods/samples/build.sh` builds the loader samples (`SampleLib`, `SampleConsumer`,
`SampleTests`, `SampleFaulty`) and copies the `resolver/*` fixtures.

| Sample | Shows |
|---|---|
| `Hello` | the first sample: ticks, game log lines, `await Context.Scheduler.NextTick()` on the tick thread |
| `SampleLib` + `SampleLib.Api` | `exports`: an API assembly (`IGreeter`, a shared `Greeters` list) dependents compile against and share at run time; IMC `echo`; every lifecycle event |
| `SampleConsumer` | a required dependency (`^1.0`), an optional absent one, shared types, an IMC round trip |
| `SampleTests` | Kiln.Testing: sync/async tests, `await t.Ticks()`, IMC and Mods API checks, a skipped test, auto-run |
| `SampleFaulty` | the fault policy: throws every 25 steps while `kiln/data/sample-faulty/fault` exists; a `missing-member` file makes it fail at load like a mod built for a newer Kiln |
| `resolver/*` | manifest-only fixtures: missing dependency, cycle, a dependent of a cycle, version mismatch, incompatible, client-only, feature, API, bad manifest |
| `HookSample` | `advanced` mods (kiln.hooks): a detour that counts calls of the game's `GetComponent`, a mid hook reading a register, a reversible byte patch, a mod-shipped symbol fragment |
| `EngineRoom` | The advanced tier's managed side in one admin command: `/engine systems` and `/engine profile` list and measure the game's systems (`GameSystems`), `/engine watch <system>` counts one system's runs through `GameSystems.Hook` (callbacks on the game's worker threads), `/engine stack <item> <count>` changes one field of an item's resource as a claim (`ReflectedResources.FindByField`, `Set`), `/engine claims` and `/engine undo` show and give back claims (`Overrides.Active`, `Claim`, `RestoreAll`); what to do when a game update renames something |
| `WorldPrograms` | an ECS program declared in `kiln.programs.json` and bound with `EcsPrograms.Bind`, emitting and reading a game event |
| `LightsOut` | tracked world writes: `/lights off` puts out every lantern, torch and candle with `World.Update<Ecs.ServerToggleState>`, and a restart keeps them out; the build fills `"writes"` |
| `Claims` | kiln.gameplay: protected areas where only their owners build, dig, open containers, loot or craft |
| `Schematics` | kiln.build: `/build` admin commands (selection, capture, paste with rotation, shapes, props, undo) and outposts placed once per world |
| `RulesSample` | Kiln.Rules: `/flight` for 60 s of the building fly mode, no stamina drain inside bases, a half-cost building weekend from a config switch |
| `NetSample` | Kiln.Network: F9 on a client pings the server over `netsample:main`; the server answers and announces Kiln clients |
| `BigStacks` | a server config (x4 stack sizes, at most 9999) that Kiln clients get through the content sync |
| `NewItems` | kiln.content's new items and recipes from code and a data pack, plus a perk and a buff of its own (kiln.gamedata clones) |
| `AffixSword` | Affix blocks: a sword that rolls one of three affixes defined in `blocks/affixes.json` (`/affix-sword`); the bonus acts while it is in hand; `/affixes` lists what the held item carries; in the player's game a notice shows the roll |
| `ItemData` | `InstanceData`: `/notch` cuts a notch into the weapon or tool in your hand and the count stays with that one item through moves, saves and restarts; `/notches` lists them. Side `both` since dev.27: the key has `Sync = InstanceSync.Holder`, so the holder's game gets the count and shows it as a notice |
| `SavedData` | The three places a mod keeps what must outlast a restart: a guestbook with the world (`WorldData`), a visit count per player by Steam id (`PlayerData`), a label on the chest next to you (`EntityData`, the chest found with `Nearby`), and a permission node of the mod's own (`Permissions.Register`). `/visits`, `/guestbook`, `/label`, `/labels`. Tested offline in `Kiln.TestKit.Tests` (`SampleModsTests`) |
| `AdminTools` | This release's player calls, each used as the contract asks: `/strike` warns, then `Players.Kick`, then `Bans.Add` for a day (the capability asked first, the result read), `/pardon`, `/strikes`; `/scout` turns Kiln's NoClip switch (`NoClip.Set`) for an admin and `PlayerNoClipEndedEvent` says why it ended |
| `CalmBell` | An interaction offer of a mod's own and a game setting changed for a while: `/bell` puts down a barrel with `Offers.SpawnWithOffer`; a player who uses it halves `enemyDamageFactor` for ten minutes through `Settings.TrySet`, and the server's own value is put back, also when the mod unloads |
| `QuietServer` | `Pacing`: a handler on the server loop's wait that lets an empty dedicated server wait 200 ms between passes instead of 40 ms, and leaves the game's pace alone once a player is online; what a pacing handler may and may not do |
| `Loadouts` | Kiln's equip and unequip: `/loadout save <name>` keeps which kind of gear a player wears in each slot (`Items.Equipped`, `PlayerData`; item types, not the single pieces, and no stacks), `/loadout wear <name>` puts gear of those kinds on again from the backpack with `Items.Equip`, `/loadout check <name>` asks about each slot as a dry run, `/loadout off <slot>` is `Items.Unequip`. The capabilities are asked first and a "not available" exception still becomes a sentence, each refusal is read and passed on, the backpack is read again before every equip, and a `PlayerRef` makes sure that after an await it is still the same player |
| `SkyControl` | Choosing the weather: `/sky` lists the weather layers (`Time.Weather`: what runs, what is held, what each layer can run), `/sky rain 10m` is `Time.SetWeather` for a while, `/sky clear` holds a clear sky, `/sky release` gives the weather back (`Time.ReleaseWeather`); what a hold is and when it ends |
| `SpellTweaks` | a kiln.gamedata data pack: Fireball I's cost, timing and explosion, and the double jump's stamina cost |
| `TypedFields` | a sword, a spell and a projectile changed through typed paths from your game's catalog, and a projectile clone; `Refused/`: a wrong string path the analyzer stops |
| `CreatureTweaks` | a data pack: Vukahs notice players from further away, and a new `Kiln_Elite_Vukah` template that replaces a share of natural spawns |
| `EliteVukah` | kiln.creatures: elite Vukahs on the game's template (level, size, ranges, resistance, loot, XP), from natural spawns or `/creature spawn elite_vukah` |
| `RuleVukah` | kiln.creatures' rules on the plain Vukah: a shout and a heal below half health, a second arsenal with a long throw at night |
| `CreatureAi` | npc-ai: who fights whom (Vukahs and Scavengers feud, a warden that hunts Scavengers, a ranger that fights for players) and rules beyond the game's kinds |
| `FireNova` | kiln.abilities: a key ability (G or `/nova`) with mana and cooldown, a ring of the game's Fireball explosions; Fireball damage doubled at night |
| `ImpactSample` | Impact programs: a heal-over-time buff written from scratch, a Fireball that chains to a second enemy, a managed Kiln native |
| `SkillTreeSample` | kiln.skills: two nodes in the game's own skill tree and a Kiln talent tree, "Ember Arts", from `skills/sample.json` |
| `NpcSample` | a friendly NPC (Tobin, a fisher clone with its own name, dialogue and roster entry), a voiced quest in the game's journal, a trade |
| `quests-sample` | a quest chain in the game's own UI from one file, `quests/sample.json`: the Warden, his topics and voice, journal, HUD and map markers; one more quest in C# |
| `FrostboltBlock` | the first blocks: a staff spell, a sword and a status sharing one Frost Burst effect, with VFX of its own; Frostlance as data |
| `AbilityBlocks` | shared effects, perks, abilities, talent trees and triggers in C# and JSON |
| `ItemBlocks` | every item block: a material, a potion, food, an arrow, tools, a weapon of every class, a wand, armor, a station and loot; `blocks/twins.json` as data |
| `WeaponMoves` | a sword with moves of its own (a chain of two swings, a heavy and a block, each with its own clip and timing) and a held model with moving parts; each clip is the game's clip of the state it is placed like, retimed to the move, with the sword arm from the sample's own rig (retargeted, the second swing's mirrored) |
| `LootBlocks` | loot tables, a creature that drops one, and recipes at the game's workbench |
| `CreatureBlocks` | reusable bodies, animation, AI, behaviour and NPCs: a creature made of its own files, a Vukah that rallies, a sentinel driven by C#, a trader |
| `StationBlocks` | a drying rack and a forge with workshops and recipes of their own, a bench, props on the game's art |
| `MenuBlocks` | menu blocks: a forge that opens the drying rack's menu, a menu part two stations share |
| `FurnitureBlocks` | a chest that wants a key, a door that opens only at night, a camp bed, seats, a wall shape with an arched window, a haircut |
| `MaterialTweaks` | how hard the game's building materials are to dig, changed on the server only (`clients: none`) |
| `SurfaceSample` | building materials that look like their own (Moss Brick, Rune Stone, snowy logs), textures made by a script |
| `TerrainSample` | terrain of a mod's own (glacier ore veins, packed snow), dug with the game's tools |
| `CosmeticSample` | a hood and a cape for the vanity slots on models made by a script: a rigid model on a joint, a `Female` morph target, a game item's look |
| `UiBlocks` | UI blocks: a settings window (F8 and the pause menu) on the mod's own config, a HUD line, buttons that cast an ability and apply an effect |
| `ScreenEffects` | screen-effect blocks: a cold tint while frozen, a white flash, a red pulse on each Bleeding tick |
| `VfxSample` | VFX blocks: an original particle effect, a light and a game VFX by name, each on a key, and a spell that uses them |
| `ShaderSample` | particle shaders (advanced): curl-noise particles moved by `shaders/swirl.hlsl`, the same block on Kiln's sprite, and a hook that fails its checks on purpose |
| `MaterialSample` | `MaterialBlock`: two swords on materials written from nothing, by hand and from a glTF's materials, and a particle that takes a material's look |
| `ModelSample` | tools/modelcook + kiln.assets: a game sword's model replaced, a new sword with its own model and visual template, icons rendered from the glTF |
| `PlayerStates` | states of a mod's own in the player's animation graph (`assets/player-states.json`): a sword whose first light attack, heavy attack and block play them (the game's clips with the sample's sword arm, from a glTF of its own rig), and a staff cast for the guided checks; cook its clips first |
| `AssetSample` | kiln.assets: a PNG icon for a new item from a data pack, a retexture of a game item's inventory icon |
| `SoundSample` | tools/soundcook + Kiln.Audio: test tones as UI, positional, looped and streamed sounds, music, a replaced game cue, a voiced line and a narration |
| `UiSample` | Kiln.Ui from code: an FPS line on the HUD, a window (F8) with widgets, toasts |
| `MenuButtons` | Kiln.Ui game UI: entries in the main and pause menus opening a screen, a crafting info panel, a subcategory and tab in the crafting menu |
| `ValueBars` | `Ui.Values` (dev.28): a water container's detail number in charges with a label, its slot bar, the health pair as a percentage, tooltip lines in Kiln's Item details window (F8) |
| `RenderSample` | render events: an FPS counter, a compass and waypoint markers in the world |

Templates: `kiln new --list` shows all 31 with their options. Block templates: `kiln-spell`, `kiln-item-blocks`,
`kiln-ability-blocks`, `kiln-creature-blocks`, `kiln-world-blocks`, `kiln-npc`, `kiln-quest`, `kiln-body`, `kiln-vfx`,
`kiln-cosmetic`, `kiln-building-material` and `kiln-screen-effect`. Code templates for one topic each: `kiln-events`,
`kiln-saved-data`, `kiln-rules`, `kiln-network`, and for the advanced tier `kiln-hooks`, `kiln-shader` and `kiln-world-program`. The others: `kiln-mod`, `kiln-test`, `kiln-unit-test`,
`kiln-datapack`, `kiln-items`, `kiln-ability`, `kiln-creature`, `kiln-talents`, `kiln-ui`, `kiln-model`, `kiln-sound`,
`kiln-native` and `kiln-service` (a service inside the Kiln repository). See [mdk.md](mdk.md).
