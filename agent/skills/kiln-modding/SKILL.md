---
name: kiln-modding
description: Build, test and debug Enshrouded mods with Kiln.Sdk, C# APIs, JSON blocks, data packs and assets. Use for Kiln mod projects, the kiln CLI, local test servers, hot reload, lint and mod-load failures; not for Kiln launcher or framework internals.
---

# Kiln modding

Kiln is a mod framework and launcher for Enshrouded. Mods are C# assemblies (.NET 10) that Kiln loads into the dedicated
server and the game client, plus optional JSON data and asset files. This skill covers mods, not Kiln's own services or the
launcher's code.

Enshrouded is a trademark of Keen Games GmbH. Kiln is unofficial.

## Before you start

1. Read the page for what you're using: https://frostdev.io/kiln/wiki. [references/docs-map.md](references/docs-map.md) says
   which one.
2. The current build is **0.2.0-dev.21**, with mod API 0.2 and the matching dev.21 MDK pack. References marked **dev.20+** or
   **dev.21+** require that runtime and MDK or newer; dev.19 lacks the dev.20 additions. Check the installed build.
   Server/offline verification does not establish the pending client rendering, input or multiplayer checks. Many dev.21
   paths were verified only under Wine (tests, dedicated-server labs), not on Windows or in a real client. The official dev.21
   launcher has Nexus integration disabled: import downloaded archives manually; no Catalog, account sign-in or `nxm://` handler.
3. Decide the side and what players need, before writing code:
   - `side`: `server` (default), `client`, or `both`;
   - `clients`: `none` (vanilla players can join), `kiln` (players need Kiln; the server sends definitions), or `mod`
     (players need this mod, from a server modpack). A stated `kiln` or `mod` makes servers refuse vanilla clients.

## Workflow

1. **Scaffold**: `kiln new <template> <Name> --server <server>` (`kiln new --list`; or `dotnet new kiln-mod`). Never by hand.
   [references/setup.md](references/setup.md).
2. **Write**: to make something new, building blocks first (`kiln new spell`, `Blocks.Define`, `blocks/*.json`; [references/docs-map.md](references/docs-map.md)): no GUIDs, no base items. `kiln blocks show <name>` says what a block became. Data over code where you can.
   [references/blocks.md](references/blocks.md) for the block families, then [references/data-packs.md](references/data-packs.md),
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

- **Tick thread.** `OnLoad`, `OnUnload` and ordinary server event handlers run on the tick thread; their `await`
  continuations return there. Game state must be touched there. ECS/system-hook callbacks instead run on game workers;
  see [references/world-programs.md](references/world-programs.md). From other threads, use `Context.Scheduler.Post(...)`
  or `await Context.Scheduler.NextTick()`. Never block the tick thread (`.Result`, `.Wait()`, `Thread.Sleep`).
- **Client threads.** Client-world work runs on the main thread (`Client.Tick`, `Client.Post`). Draw functions run every frame:
  keep them short.
- **Register in `OnLoad`, work later.** On a client `OnLoad` can run in the main menu. Do world work from
  `ServerStartedEvent` or `Client.Tick`.
- **Registrations belong to one mod lifetime.** Do not retain callbacks or handles from an old load. Release threads, timers,
  sockets, files and static references held by non-mod code. ECS bindings drain callbacks at unload but keep an inert scheduler
  entry until restart; see [references/world-programs.md](references/world-programs.md).
- **Item state (dev.20+).** Use `Items.Move` for gear transfers, not take + give. Item-entity `EntityData` is session-only; no persistent
  per-item payload or affix API is supplied. [references/api.md](references/api.md#inventory-transfers-dev20).
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
- An advanced mod: `kiln/state/hooks.json` shows your link on the right function (and `linkSlots.used` does not grow on each
  command), and kiln.log has no `hooks:` warning you didn't expect.

## When something isn't documented

Don't guess at game internals. Read the wiki and the XML docs that ship with Kiln.Api. If a service doesn't offer what you need,
say so instead of patching game memory. Low-level hooks are for mods that opt into the advanced tier (`"advanced": true`);
the short rules (kiln.hooks v2: link order, `caller()`, link slots, `hooks.json`; closed DLL mods and `[late]`; EML and EMF
notes) are at the end of [api.md](references/api.md).
