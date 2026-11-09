# Writing Kiln mods

Kiln loads C# mods into the Enshrouded server and client. Each feature page states what still needs a client check. This folder is the modder's guide; the
framework's internals are in `docs/kiln/architecture.md`.

| Page | What |
|---|---|
| [mdk.md](mdk.md) | The MDK: `dotnet new kiln-mod`, Kiln.Sdk, build, deploy, pack, local NuGet feed |
| [manifest.md](manifest.md) | `kiln.mod.json` v2: fields, version ranges, dependencies, load order, exports, sides |
| [lifecycle.md](lifecycle.md) | Mod and server lifecycle events, the `Mods` API, inter-mod messages, hot reload |
| [testing.md](testing.md) | Kiln.Testing: `[KilnTest]` methods that run inside the server |
| [faults.md](faults.md) | Fault policy, crash reports, `kiln.json` `mods` settings |
| [blocks.md](blocks.md) | Building blocks: effects, statuses, spells, abilities, perks, talents, bodies, loot and UI; what they compile to, client requirements and errors. Start here to make something |
| [item-blocks.md](item-blocks.md) | Items, weapons, wands, armor, stations, loot tables and recipes as blocks: no base items, no GUIDs |
| [world-blocks.md](world-blocks.md) | Props, crafting props, stations, containers with default loot and lids, doors, colliders and building materials |
| [npc-blocks.md](npc-blocks.md) | Creature bodies, animation, AI, behaviour, drops, friendly NPCs and vendors |
| [ui-blocks.md](ui-blocks.md) | Windows, HUD lines and key bindings in C# or JSON; server validation and the client checklist |
| [inventory.md](inventory.md) | Item-state-preserving moves, container names, transfer rules/events and current-session gear data limits |
| [cosmetics.md](cosmetics.md) | Pieces for the vanity slots on models of your own: skinned or on one joint, both bodies from one file, `kiln skeleton player` |
| [impact.md](impact.md) | Impact programs: tools/impact (dis/asm/lint/clone/cook), Kiln natives, packaging into kiln.assets |
| [sounds.md](sounds.md) | Sounds, cues, music, voice-over: tools/soundcook, cook.json sound steps, SoundSample |
| [vfx.md](vfx.md) | Effects: particles, lights and the game's VFX by name, in `blocks/*.json` or C#, the budget, VfxSample |
| [screen-effects.md](screen-effects.md) | Full-screen effects: colour grading, tint, vignette, fades and flashes, on a status, an effect or from code |
| [models.md](models.md) | Weapons and creature bodies from glTF, rigid or skinned: tools/modelcook, cook.json model steps |
| [materials.md](materials.md) | New surfaces for models from PNGs and numbers, on the game's own shaders: no base to name, or made from a glTF's materials |
| [animations.md](animations.md) | Clips, skeletons and state-machine graphs from glTF: tools/animcook, cook.json animation steps |
| [quests.md](quests.md) | Quest chains, conversations and NPCs: QuestBlock/DialogueBlock, quests/*.json, Quests.Define, rewards and client requirements |
| [lint.md](lint.md) | `kiln lint`: usage, output, JSON dialects, every rule with an example and the fix; [schemas/](schemas/) is the field reference of each format |
| [vscode.md](vscode.md) | The VS Code extension (tools/vscode): schemas, lint, game ids, build/deploy/reload, Testing view, kiln.log, KAP/1 console, Impact assembly |
| [cli.md](cli.md) | The `kiln` command: new, build, cook, deploy, run, reload, test, debug, lint, pack, logs, catalog |
| [analyzers.md](analyzers.md) | KILN1xx: compiler warnings about threads, hot reload and the manifest, with code fixes |
| [catalogs.md](catalogs.md) | The local game-data catalog and the game-id constants |
| [typed-fields.md](typed-fields.md) | The game's data as typed C# structures, for what blocks don't cover: `GameFields`, `GameEnums`, checked string paths |
| [player-states.md](player-states.md) | Every state and action a player can be in: `player.Is(...)`, `PlayerStateChangedEvent`, `PlayerActionEvent`, and what is left out |
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

## Samples (`mods/samples/`, built by `mods/samples/build.sh`)
| Sample | Shows |
|---|---|
| `Hello` | the first sample: ticks, game log, `await NextTick()` |
| `SampleLib` + `SampleLib.Api` | `exports`: an API assembly dependents compile against and share at run time; IMC `echo`; every lifecycle event |
| `SampleConsumer` | a required dependency (`^1.0`), an optional absent one, shared types, an IMC round trip |
| `SampleTests` | Kiln.Testing: sync/async tests, `await t.Ticks()`, IMC and Mods API checks, a skipped test, auto-run |
| `AbilityBlocks` | Shared effects, perks, abilities, talent trees and triggers in C# and JSON |
| `CreatureBlocks` | Reusable bodies, animation, AI, behaviour and NPCs |
| `ScreenEffects`, `MenuBlocks`, `UiBlocks` | Screen-effect blocks, menu integration, windows, HUD lines and keys; client drawing/input checks remain |
| `SampleFaulty` | the fault policy: throws every tick while `kiln/data/sample-faulty/fault` exists |
| `resolver/*` | manifest-only fixtures: missing dependency, cycle, version mismatch, incompatible, client-only, feature, API, bad id |

Block templates include `kiln-item-blocks`, `kiln-world-blocks`, `kiln-npc`, `kiln-creature-blocks`, `kiln-cosmetic`,
`kiln-building-material` and `kiln-ability-blocks`. `kiln new --list` shows their current options; see [mdk.md](mdk.md).
