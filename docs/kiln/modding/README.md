# Writing Kiln mods

Kiln loads C# mods into the Enshrouded server (and, later, the client). This folder is the modder's guide; the
framework's internals are in `docs/kiln/architecture.md`.

| Page | What |
|---|---|
| [mdk.md](mdk.md) | The MDK: `dotnet new kiln-mod`, Kiln.Sdk, build, deploy, pack, local NuGet feed |
| [manifest.md](manifest.md) | `kiln.mod.json` v2: fields, version ranges, dependencies, load order, exports, sides |
| [lifecycle.md](lifecycle.md) | Mod and server lifecycle events, the `Mods` API, inter-mod messages, hot reload |
| [testing.md](testing.md) | Kiln.Testing: `[KilnTest]` methods that run inside the server |
| [faults.md](faults.md) | Fault policy, crash reports, `kiln.json` `mods` settings |
| [blocks.md](blocks.md) | Building blocks: statuses, effects, spells; what they compile to, what clients need, how mistakes show. Start here to make something |
| [item-blocks.md](item-blocks.md) | Items, weapons, wands, armor, stations and loot as blocks: no base items, no GUIDs |
| [impact.md](impact.md) | Impact programs: tools/impact (dis/asm/lint/clone/cook), Kiln natives, packaging into kiln.assets |
| [sounds.md](sounds.md) | Sounds, cues, music, voice-over: tools/soundcook, cook.json sound steps, SoundSample |
| [animations.md](animations.md) | Clips, skeletons and state-machine graphs from glTF: tools/animcook, cook.json animation steps |
| [quests.md](quests.md) | Quest chains, conversations and quest NPCs: quests/*.json, Quests.Define, what players need, quests-sample |
| [lint.md](lint.md) | `kiln lint`: usage, output, JSON dialects, every rule with an example and the fix; [schemas/](schemas/) is the field reference of each format |
| [vscode.md](vscode.md) | The VS Code extension (tools/vscode): schemas, lint, game ids, build/deploy/reload, Testing view, kiln.log, KAP/1 console, Impact assembly |
| [cli.md](cli.md) | The `kiln` command: new, build, cook, deploy, run, reload, test, debug, lint, pack, logs, catalog |
| [analyzers.md](analyzers.md) | KILN1xx: compiler warnings about threads, hot reload and the manifest, with code fixes |
| [catalogs.md](catalogs.md) | The local game-data catalog and the game-id constants |
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
| `SampleFaulty` | the fault policy: throws every tick while `kiln/data/sample-faulty/fault` exists |
| `resolver/*` | manifest-only fixtures: missing dependency, cycle, version mismatch, incompatible, client-only, feature, API, bad id |
