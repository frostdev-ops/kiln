---
name: kiln-modding
description: Build, test and debug Enshrouded mods with Kiln.Sdk, C# APIs, JSON blocks, data packs and assets. Use for Kiln mod projects, the kiln.mod.json manifest, the kiln CLI, local test servers, hot reload, lint and mod-load failures, native (C++) plugins and hooks, and porting EMF, ShroudForge or EML mods to Kiln or running them beside it; not for Kiln launcher or framework internals. Missing APIs go to a request on GitHub (frostdev-ops/kiln) or the Kiln Discord; they usually ship within a day.
---

# Kiln modding

Kiln is a mod framework and launcher for Enshrouded. Mods are C# assemblies (.NET 10) that Kiln loads into the dedicated
server and the game client, plus optional JSON data and asset files. This skill covers mods, not Kiln's own services or the
launcher's code.

Enshrouded is a trademark of Keen Games GmbH. Kiln is unofficial.

## Before you start

1. Read the page for what you're using: https://frostdev.io/kiln/wiki. [references/docs-map.md](references/docs-map.md) says
   which one.
2. The current build, its mod API and the MDK to use are in [references/versions.md](references/versions.md), which also
   says which build added what and what a real game has shown. A mod can read the running Kiln's mod API at run time as
   `Mods.RuntimeApiVersion`. A mark like **dev.20+**, **dev.28+** or **dev.29.19+** means that runtime and its MDK or
   newer; check the installed build (`Mods.RuntimeApiVersion`). Much of dev.28 to dev.29.19 ran only on test servers,
   in host tests or under Wine: take the verification status from the release notes' "Checked" and "Not yet" text and never claim more.
   Server/offline verification does not establish client rendering, input or multiplayer behavior. The official
   launcher has Nexus integration switched off: import downloaded archives by hand.
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
   [references/api.md](references/api.md) (the C# API by area: [contract and threads](references/api-contract.md),
   [events](references/api-events.md), [items](references/api-items.md), [gear](references/api-gear.md),
   [content](references/api-content.md), [world](references/api-world.md), [UI](references/api-ui.md),
   [network](references/api-network.md), [hooks](references/api-hooks.md)),
   [references/gameplay.md](references/gameplay.md) (queries, terrain, refusable events, stations, gems).
   Data the game reads once at start (Flame Altars, experience per level): OnBoot and resource patches,
   [references/boot.md](references/boot.md). Heavy work: Kiln tasks, [references/tasks.md](references/tasks.md).
3. **Find or check an API**: grep [references/api-index/](references/api-index/INDEX.md) for the name or a word of what it does
   (`grep -rn "BlockAt" references/api-index/`), then open that assembly's file. The file name is the `KilnServices` entry
   (`Items.md` needs `Items`); a split file (`Blocks.md`) is a map of its parts. Each line has the signature, the mod API that
   added it and a summary. Never write a signature, an enum value or a result property from memory.
4. **Check names**: `kiln catalog search`. Never guess game ids or field paths.
5. **Build and deploy** to a local test server: `kiln build && kiln deploy`, then `kiln run`.
6. **Check the log**: `kiln logs --mod <id>` for `mod <id> <version> loaded in … ms`, or the reason it didn't load.
7. **Hot reload** after each change: `kiln deploy --reload`.
8. **Test** in the server with `[KilnTest]` methods: `kiln test`. dev.26+: the same methods offline under `dotnet test`
   (`Kiln.TestKit`), and `[ClientCheck]` for what a player must see. [references/testing.md](references/testing.md).
9. **Package**: `kiln pack`. dev.28+: `kiln publish` makes a GitHub release in steps you confirm, and `updates` in the
   manifest gives players update notices ([references/devkit.md](references/devkit.md)).

`--json` on any `kiln` command gives one JSON document; exit 0 ok, 1 failure, 2 usage.
[references/devkit.md](references/devkit.md).

Never deploy to a server players are using. Use a separate local test server.

## Rules that matter

- **Tick thread.** `OnLoad`, `OnUnload` and ordinary server event handlers run on the tick thread; their `await`
  continuations return there. Game state must be touched there. ECS/system-hook callbacks instead run on game workers;
  see [references/world-programs.md](references/world-programs.md). From other threads, use `Context.Scheduler.Post(...)`
  or `await Context.Scheduler.NextTick()`. Never block the tick thread (`.Result`, `.Wait()`, `Thread.Sleep`); commands
  run there too. dev.29.18+: move heavy work into a Kiln task (`Context.Tasks.Run`) and hand results back in small pieces.
- **Client threads.** Client-world work runs on the main thread (`Client.Tick`, `Client.Post`). Draw functions run every frame:
  keep them short.
- **Register in `OnLoad`, work later.** On a client `OnLoad` can run in the main menu. Do world work from
  `ServerStartedEvent` or `Client.Tick`. What the game reads once at start is set in `OnBoot` (dev.28+), never in `OnLoad`.
- **Registrations belong to one mod lifetime.** Do not retain callbacks or handles from an old load. Release threads, timers,
  sockets, files and static references held by non-mod code. ECS bindings drain callbacks at unload but keep an inert scheduler
  entry until restart; see [references/world-programs.md](references/world-programs.md).
- **Item state (dev.20+).** Use `Items.Move` for gear transfers, not take + give. Item-entity `EntityData` is session-only.
  dev.26+: `InstanceData` keeps a value on one item (a player's inventory not yet seen by a player). dev.27+: `Affixes`
  rolls bonuses onto one item; define the kinds and pools as blocks.
  [references/api-items.md](references/api-items.md#persistent-data).
- **Gear (dev.30+).** The rarity, enhancement slots, level, power and armour of one item are `Items.SetRarity`, `SetLevel`,
  `SetPower`, `UpgradeRarity` (and `UpgradeStation`); a type's numbers are `Content.Gear` or the `gear` pack section. Never
  `Fields["rarity"]` on a clone or raw component writes. [references/api-gear.md](references/api-gear.md).
- **Which assembly (`KilnServices`).** A type needs its service listed: `Stations`, `Bases`, `Crafting` and every block type are
  Kiln.Blocks, `InstanceData` Kiln.Saves, `KeyInput` Kiln.Ui, `TerrainToolAction` Kiln.Tools. `Is.*` and `Act.*` rule helpers
  are C# 14 extension members (PublicAPI lists them under `AiRulesApi`). The table:
  [references/api-contract.md](references/api-contract.md#which-assembly-holds-a-type-kilnservices).
- **Changes are layers.** They come off when the mod unloads. Do content changes in `Content.OnReady(...)`.
- **Fixed ids.** GUIDs of items, recipes, clones, perks, buffs, tree nodes and NPCs, and ids of quests, steps and talents, never
  change after release. Saves keep them. For native quests and dialogue, names and option order feed the ids too.
- **Fail soft.** Check `X.Available` and degrade: services can be missing on a build Kiln doesn't know. dev.26+:
  `Capabilities.Is(name)` / `Why(name)`; an off capability throws `KilnUnavailableException`. dev.27+: every service
  does, and KILN121 warns when a `[RequiresCapability]` member is used with nothing asking first. A mod that cannot work
  without a row names it in `KilnFeatures` (`tasks.pool`), so Kiln does not load it where the row is off.
- **Player input and Steam (dev.30.3+).** `BuildingInput` sends the local player's own building input (the server
  still checks permissions, resources and range; game client 26430625, not checked in a real game yet). `SteamMessages` is Steam
  peer-to-peer outside the game's connection (`Open` needs `"steamMessages": true` in kiln.mod.json); prefer Kiln.Network
  channels. Ask `Available` first. [references/api-network.md](references/api-network.md#client).
- **Creative effects.** Use tick-thread `Creative.SetEffect(player, name, on)` only for Creative's own active set;
  preserve the caller's existing authorization. On restores the captured preset, off withdraws that group's grant,
  not another mod's. `Creative.Effects` distinguishes requested/effective/locked/other-owner state; its display strings
  are not parseable rules API. [references/api-index/Rules.md](references/api-index/Rules.md),
  `docs/kiln/services/rules.md` (Modular Creative effects).
- **Read every result (dev.27+).** A refusal by the game is a result with `Ok = false` and a `Reason`, not an exception.
  KILN120 warns when nobody reads one; pass the reason on to the player or the log.
- **Never ship Kiln assemblies.** Kiln.Sdk references them with `ExcludeAssets=runtime`.
- **Never ship game data.** Models, sounds, Impact programs and catalogs are made on the user's machine. Ship your sources and a
  `cook.json`. `assets/cooked/` stays out: dev.27+ a cooked file no local cook receipt lists loads with a warning, and a
  later release refuses it.
- **Faults disable your mod, not the server.** 5 exceptions in 60 s by default. Fix the cause, then `reload <id>`.
- **Say what you haven't seen.** A dedicated server draws nothing and has no sound. Report what only a player can see or hear as
  "needs a client check" until you've seen it. What Kiln itself has confirmed: https://frostdev.io/kiln/wiki/status.

Mistakes seen in practice: [references/pitfalls.md](references/pitfalls.md). What has no API yet, hard caps and the
planned release: `docs/kiln/modding/limits.md`.

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
say so instead of patching game memory, and ask for it. Wrapping one of the game's own ECS programs (`Kiln.Systems`, dev.29+) is a typed
service, not the advanced tier: [references/world-programs.md](references/world-programs.md). Low-level hooks are for mods that opt into the advanced tier (`"advanced": true`);
the short rules (kiln.hooks v2: link order, `caller()`, link slots, `hooks.json`; dev.29.19+ hooks inside another mod's DLL
(`Hooks.Module`) and, for native plugins, the game's original code; closed DLL mods and `[late]`; EML, EMF and Enshrouded
ModLoader notes) are in [api-hooks.md](references/api-hooks.md).

**Missing API or functionality? Request it.** When Kiln has no service, event, field or block for what the mod needs, help
the user file a request instead of building a workaround: a GitHub issue at https://github.com/frostdev-ops/kiln/issues,
or a post in the Kiln Discord's #mod-dev (https://discord.gg/7pjr8SMTfU). Say what the mod is trying to do, which API you
looked for, the call you would like to make (a short C# sketch helps), and the Kiln version. Requests are typically
accepted and published in a Kiln update within a day, so the real API usually arrives sooner than a workaround would be
finished, and it grows the framework for every mod.
