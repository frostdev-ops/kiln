---
name: kiln-modding
description: Build, test and debug Kiln mods for Enshrouded. Use when writing or changing a Kiln mod (C# with Kiln.Sdk, kiln.mod.json, content/*.json data packs, game data, new items, creatures, abilities, talents, quests, friendly NPCs, sounds, particles and VFX, game UI, assets, world edits), when using the kiln command, kiln lint, the analyzers or the debugger, when deploying to a local test server, hot reloading, running in-server tests, or reading kiln.log to find out why a mod did not load.
---

# Kiln modding

Kiln is a mod framework and launcher for Enshrouded. Mods are C# assemblies (.NET 10) that Kiln loads into the dedicated
server and the game client, plus optional JSON data and asset files. This skill covers mods, not Kiln's own services or the
launcher's code.

Enshrouded is a trademark of Keen Games GmbH. Kiln is unofficial.

## Before you start

1. Read the page for what you're using: https://frostdev.io/kiln/wiki. [references/docs-map.md](references/docs-map.md) says
   which one.
2. Today's mod API is 0.2 (Kiln.Sdk 0.2.0). Within a major version it only grows.
3. Decide the side and what players need, before writing code:
   - `side`: `server` (default), `client`, or `both`;
   - `clients`: `none` (vanilla players can join), `kiln` (players need Kiln; the server sends definitions), or `mod`
     (players need this mod, from a server modpack). A stated `kiln` or `mod` makes servers refuse vanilla clients.

## Workflow

1. **Scaffold**: `kiln new <template> <Name> --server <server>` (`kiln new --list`; or `dotnet new kiln-mod`). Never by hand.
   [references/setup.md](references/setup.md).
2. **Write**: to make something new, building blocks first (`kiln new spell`, `Blocks.Define`, `blocks/*.json`; [references/docs-map.md](references/docs-map.md)): no GUIDs, no base items. `kiln blocks show <name>` says what a block became. Data over code where you can.
   [references/data-packs.md](references/data-packs.md),
   [references/quests-talents-sounds.md](references/quests-talents-sounds.md),
   [references/vfx.md](references/vfx.md),
   [references/api.md](references/api.md).
3. **Check names**: `kiln catalog search`. Never guess game ids or field paths.
4. **Build and deploy** to a local test server: `kiln build && kiln deploy`, then `kiln run`.
5. **Check the log**: `kiln logs --mod <id>` for `mod <id> <version> loaded in … ms`, or the reason it didn't load.
6. **Hot reload** after each change: `kiln deploy --reload`.
7. **Test** in the server with `[KilnTest]` methods: `kiln test`. [references/testing.md](references/testing.md).
8. **Package**: `kiln pack`.

`--json` on any `kiln` command gives one JSON document; exit 0 ok, 1 failure, 2 usage.
[references/devkit.md](references/devkit.md).

Never deploy to a server players are using. Use a separate local test server.

## Rules that matter

- **Tick thread.** `OnLoad`, `OnUnload`, handlers and every `await` in mod code run on the server's tick thread. Game state
  must be touched there. From other threads, use `Context.Scheduler.Post(...)` or `await Context.Scheduler.NextTick()`. Never
  block it (`.Result`, `.Wait()`, `Thread.Sleep`).
- **Client threads.** Client-world work runs on the main thread (`Client.Tick`, `Client.Post`). Draw functions run every frame:
  keep them short.
- **Register in `OnLoad`, work later.** On a client `OnLoad` can run in the main menu. Do world work from
  `ServerStartedEvent` or `Client.Tick`.
- **Everything registered is removed at unload.** Release what you created: threads, timers, sockets, files, static references
  held by non-mod code.
- **Changes are layers.** They come off when the mod unloads. Do content changes in `Content.OnReady(...)`.
- **Fixed ids.** GUIDs of items, recipes, clones, perks, buffs, tree nodes and NPCs, and ids of quests, steps and talents, never
  change after release. Saves keep them. For native quests and dialogue, names and option order feed the ids too.
- **Fail soft.** Check `X.Available` and degrade: services can be missing on a build Kiln doesn't know.
- **Never ship Kiln assemblies.** Kiln.Sdk references them with `ExcludeAssets=runtime`.
- **Never ship game data.** Models, sounds, Impact programs and catalogs are made on the user's machine. Ship your sources and a
  `cook.json`. `assets/cooked/` stays out.
- **Faults disable your mod, not the server.** 5 exceptions in 60 s by default. Fix the cause, then `reload <id>`.
- **Say what you haven't seen.** A dedicated server draws nothing and has no sound. Report what only a player can see or hear as
  "needs a client check" until you've seen it. What Kiln itself has confirmed: https://frostdev.io/kiln/wiki/status.

Mistakes seen in practice: [references/pitfalls.md](references/pitfalls.md).

## Checks before you call it done

- `kiln build` passes with no `KILN0xx` errors and no `KILN1xx` warnings you didn't mean.
- `kiln lint` is clean (with a catalog: `kiln catalog build`).
- The log shows the mod loaded, no `[E] [mod:<id>]` lines and no data-pack problems.
- `kiln test` passes, also after a hot reload.
- `echo "reload-test <id> 10" > kiln/control/rt.cmd` reports no leaked load contexts.

## When something isn't documented

Don't guess at game internals. Read the wiki and the XML docs that ship with Kiln.Api. If a service doesn't offer what you need,
say so instead of patching game memory. Low-level hooks are for mods that opt into the advanced tier (`"advanced": true`) and are
out of scope here.
