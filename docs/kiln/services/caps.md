# kiln.caps / Capabilities: what works here, and why not

One table of capabilities for the whole process: a name with a state (available, unavailable, pending), a reason code,
a reason text and a source. The core fills it; services, plugins and Kiln's managed layer add rows. Mods read it as
`Kiln.Capabilities`; the same table is written to `kiln/state/capabilities.json`.

**Status (dev.26):** works on a test server (a dedicated lab server under Wine). Not run in a game client.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_caps.h` (`kiln.caps` v1, in-core: always there, also in safe idle) |
| Native | `native/core/src/services/caps.cpp` (the registry, the core's rows, the file writer); `sigdb.cpp` gives each feature's reason |
| Managed | `managed/Kiln.Api/Capabilities.cs` (`Capabilities`, `Capability`, `CapabilityState`, `CapabilityReason`, `CapabilityChangedEvent`, `RequiresCapabilityAttribute`); `Game.HasFeature` asks the same table |
| Loader | `managed/Kiln.Host/Resolver.cs`: a manifest's `features` are checked against it ([modding/manifest.md](../modding/manifest.md#asking-what-works)) |
| File | `kiln/state/capabilities.json`, `schema/state-capabilities.schema.json`, example `schema/examples/state-capabilities.json` |
| Tests | `native/core/tests/test-caps.sh` (host, no game), `test-caps-lab.sh <lab> [--with-mods] [--safe-idle]` (lab); `managed/tests/Kiln.Api.Tests/CapabilitiesTests.cs`, `Kiln.Host.Tests/CapabilityResolverTests.cs` |

## C#
```csharp
if (!Capabilities.Is("items.fuel")) { Log.Warn(Capabilities.Why("items.fuel")); return; }
Capability row = Capabilities.Get("kiln.items");       // State, Reason, ReasonText, Source, SessionDependent
foreach (var c in Capabilities.All) { /* every row, sorted by name */ }
Events.On<CapabilityChangedEvent>(e => Log.Info($"{e.Name}: {e.Previous.State} -> {e.Current.State}"));
```
- `Is`, `Get`, `Why` and `All` work on any thread; a read is a dictionary lookup in a snapshot, no native call.
- A name with no row: `Is` is false, `Get` answers `Unavailable` with reason `Unknown`.
- `Why` gives one sentence to log, for example
  `items.fuel is unavailable: symbol items/FuelTick: find[0]: 0 matches (symbol-missing, from sigdb).`
- `CapabilityChangedEvent` is posted on the bus for each row that is new, changed or removed, on the tick thread at the
  start of the next step, and only while someone subscribes.
- `[RequiresCapability("name")]` marks a member that throws `KilnUnavailableException` naming that capability when it
  is off. It is documentation: nothing checks it at build or run time.

## Names
Case-sensitive, 1 to 96 bytes of `[A-Za-z0-9._/-]`:
- a sigdb feature keeps its sigdb name: `items.fuel`, `net.kick`, `tick`, `world.lifecycle`;
- `kiln.<service>`: that service's table is here (`kiln.items`, `kiln.hooks`);
- `<service>.<thing>`: a row a service registers for an ability that is not a sigdb feature. Where a sigdb feature
  exists, its name is used, so there is one row per ability;
- anything else a plugin reports starts with its own id: `<id>.<thing>`.

## States and reasons
| State | Meaning |
|---|---|
| `available` | it works; the text may carry a note (a service's version, `v3`) |
| `unavailable` | it does not work here; reason and text say why |
| `pending` | it depends on the session and is not decided yet |

| Reason | When |
|---|---|
| `none` | only with `available` |
| `symbol-missing` | a symbol or constant of the feature did not resolve on this game build; the text names the first one that failed: `symbol <name>: <sigdb's reason>` |
| `service-missing` | the service is not here (its DLL is not installed, was skipped, failed to load, or its init said no) |
| `service-older` | the service is here but older than the caller needs |
| `wrong-side` | server only on a client, or client only on a server |
| `needs-kiln-client` | works only for players whose client runs Kiln |
| `policy` | switched off by configuration or by the build policy (safe idle on an unverified build) |
| `faulted` | it was on and faulted, or its hook could not be installed |
| `offline` | no game in this process (the test kit, a tool) |
| `session` | waits for the session (no world yet, world data not loaded); the usual reason of a pending row |
| `unknown` | no row of this name; never stored, only `Capabilities.Get` answers it |

## The client gate (dev.28)
A capability can be **held** until its guided client checks
([client-pass.md](../client-pass.md)) have passed in a real game. The list is empty in 0.2.0-dev.29:
all 22 capabilities held in 0.2.0-dev.28 are on now, subject to their normal dependencies and side restrictions.
Opening the remaining 20 is an owner decision, not a real-game confirmation; their guided checks remain unconfirmed.

- **The list** is `native/core/gates/*.json` (one file per work area; `README.md` there has the format): a capability
  name, the ids of the checks that open it, optionally the one side it is held on. It is compiled into
  `kiln-core.dll`, so an install cannot lose it. `python3 native/core/gates/gen.py --list native/core/gates` prints it.
- **While a name is held** the core's own report for it is `unavailable`, reason `policy`, text
  `game test pending: <check ids>`. That is what every reader sees, whatever a service reports for the name and
  although a sigdb feature's symbols resolved; `KilnApi.feature()` answers false for it. A held name may still be
  reported pending by its service (the held row hides it).
- **Reading it:** `Capabilities.IsHeld(name)` and `Capability.IsHeld` in C#; `kiln_caps_held(caps, name)` in
  `kiln_caps.h`. A service installs the hooks of a held capability only when that says no. There is no second switch:
  no config key of a service's own, no sigdb overlay.
- **Lifting it:** `"preview"` in `kiln.json`, at the top level or in a side section (which replaces the top-level
  one): `true` for every held capability, or a list of names. A name in the list that nothing holds is a warning in
  `kiln.log`. No preview setting is needed for this release’s guided pass.
  ```json
  { "preview": ["camera.fov"], "client": { "preview": true } }
  ```
- **Opening a capability for good:** remove its entry from the list in the commit that records the passed checks.

## Rows the core fills
- Every sigdb feature, source `sigdb`. An unavailable one says `symbol-missing` with the symbol, `policy` in safe idle,
  or `faulted` for the `tick*` features and `world.lifecycle` when their hook is not installed or faulted.
- Every in-core service and every published service, `kiln.<service>`, with `v<n>` as text.
- `world.stop_drain.server`, `world.stop_drain.client`, `world.stop_drain.prediction` (since 0.2.0-dev.29): what the core
  guarantees about a world kind's jobs when it dispatches that world's `KILN_WORLD_STOPPING`. Available: drained by the
  engine (the core runs the game's WaitWorldJobs on the server and the client world before their STOPPING; the
  prediction world stops with the client world, after that wait). Unavailable: `symbol-missing` "bounded wait only"
  (WaitWorldJobs did not resolve: a subscriber's own bounded wait is all there is), `faulted` or `symbol-missing` "no
  world stop is seen" (the tick or world-destroy hook is not there), `wrong-side` for the client kinds in a dedicated
  server, `policy` in safe idle. They follow a hook that faults later, as the `tick*` rows do. kiln.world names the row
  in its wrap capability texts and at each stop.
- Every service DLL that did not load, as `kiln.<stem>`, with `service-missing` or `faulted`.
- Kiln's managed layer adds `identity.entities` and `identity.players` ([modding/contract.md](../modding/contract.md)):
  pending until a service registers a source, then available. The sources (Kiln.Players, Kiln.Items, Kiln.Spawn)
  register at the first service tick. A source can still answer "not yet" for one entity: kiln.spawn's says so while
  the server world is still loading its save, and the call throws `KilnUnavailableException("identity.entities")`.

## Rows added in dev.26
| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `items.fuel` | sigdb feature | core; Kiln.Items lowers it at its start when its table has no water functions or `items.transaction` is off | water in containers (`Items.Fuel`) |
| `items.move` | sigdb feature | core; Kiln.Items lowers it when its table has no move functions or `items.transaction` is off | moves that keep item state (`Items.Move`) |
| `items.names` | sigdb feature | core; Kiln.Items lowers it when its table has no name functions or kiln.items turned them off | container names |
| `items.transfer` | sigdb feature | core; Kiln.Items lowers it when its table has no transfer functions or kiln.items turned the hooks off | the quick-stack and move-all hooks (`Items.Transfers`) |
| `items.instances` | sigdb feature | core; Kiln.Items lowers it when its table is older than v4 or kiln.items turned item instances off | item instances: stamps, locations, save-record fields (`ItemInstanceRef`, `Items.Held`, `Items.Equipped`) |
| `items.result_peek` | managed, static | Kiln.Items at its start | its table has `peek_result` (an older runtime's has not) |
| `items.moves` | managed, session | Kiln.Items, refreshed every 25 steps | moves run right now: available while the `kiln_items` program runs in a server world, pending while it waits for one, unavailable when it broke |
| `saves.instance_data` | managed, session | Kiln.Saves at its start and about once a second | `InstanceData` works: kiln.saves and an item source (`items.instances`, or kiln.spawn's) are here and the world's data is open; pending while it is not open (a hosted world before the game loads it) |
| `saves.instance_data.carried` | managed, static | Kiln.Saves | always unavailable (`policy`): `Persist.Carried` is not in this release |
| `anim.player_states` | native, static | kiln.playeranim, decided before the mods load and never pending | mods' states in the player's animation graph ([playeranim.md](playeranim.md)) |
| `blocks.weapon_moves` | managed, static | Kiln.Blocks, once, when the first item block compiles | a weapon's moves, a wand's and a staff cast's animation and `hitAt`: needs kiln.gamedata |
| `blocks.animated_held` | managed, static | Kiln.Blocks, with the row above | a look's animated held model: needs kiln.gamedata and kiln.assets |

## Rows added in dev.27
| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `rules.noclip` | sigdb feature | core; kiln.rules lowers it at the first world when its hook did not go in: `policy` when character flight's hooks are off (`rules.flight` did not resolve, or the Creative Mode mod runs), `faulted` when the hook could not be installed or the layout is not the expected one | NoClip works on this build and side (`NoClip.Available`, `PlayerRules.NoClip`; [rules.md](rules.md)). Whether one player gets it is still the server's `rules.json` and that player's game: `NoClip.Set` says |
| `time.weather_choose` | sigdb feature | core; Kiln.Time's members throw `KilnUnavailableException` naming it when its table is older than v2 | the weather table, each layer's phenomenon, choosing one (`Time.WeatherPhenomena`, `Time.Weather`, `Time.SetWeather`, `Time.ReleaseWeather`, `WeatherChangedEvent`); `time.weather` stays the reset (`Time.ResetWeather`) |
| `items.equip` | sigdb feature | core; Kiln.Items lowers it at its start when its table is older than v5 or kiln.items cannot run equip (no kiln.world or kiln.players, no death check, the program refused), later when the program breaks, and for the session on a game client with no server world (reason `session`; back when the client hosts) | equip and unequip through the game's own functions (`Items.Equip`, `Items.Unequip`); a manifest can require it like the other sigdb features |

What a manifest's `features` can require of these: the sigdb features (`items.fuel`, `items.move`, `items.names`,
`items.transfer`, `items.instances`) and `anim.player_states`. The manifests are checked before the managed services
start, so for a sigdb feature the check sees the sigdb verdict only, not a later lowering by Kiln.Items: ask
`Capabilities.Is` at run time as well. The managed rows exist only once their service has started (`blocks.*` only
after the first item block compiled), and `items.moves` and `saves.instance_data` depend on the session: a manifest
naming one of them is refused, with the advice to check `Capabilities.Is` at run time.

## Rows added in dev.27
| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `abilities.buffs` | managed, session | Kiln.Abilities, at its start (the first service tick; before dev.27's adoption: at its first use), at every call that needs it and about once a second | buffs removed (and applied) through the game's natives (`Abilities.RemoveBuff`, `Abilities.ApplyBuff`): needs kiln.abilities with its natives, programs and instance hooks (`abilities.natives`, `abilities.programs`, `abilities.instances`) and a kiln.spawn that takes extra components (`service-older` otherwise). Pending while no server world has set up the game's native and program registries (a client in its menu: mods load there, the world comes later) |
| `abilities.buff_list` | managed, static | Kiln.Abilities, at its start (the first service tick) | an entity's buffs read (`Abilities.BuffsOf`): needs kiln.playerstate (`service-missing` without it) and its reflection reads (`players.reflection`; `faulted` when the service is there and read nothing) |
| `players.kick` | managed, static | Kiln.Network, at its start (the first service tick) | `Players.Kick`, `Network.Kick`: needs kiln.net, `net.kick`, `net.players` (the game's connection records, which say whose connection one is) and a process that serves clients (`wrong-side` on a client that only joins) |
| `players.bans` | managed, static | Kiln.Network, with the row above | Kiln's ban list is enforced (`Bans`); the same needs |
| `combat.hold_filter` | managed, static | Kiln.Combat, at its start (the first service tick; before dev.27's adoption: when its listener started) | kiln.combat holds only the hits a handler's entity filter lets through (`hold_filter`, appended in dev.27); off: filtered handlers still get only their entities' hits, but every hit waits a step |
| `players.aim` | managed, static | Kiln.Players, at its start (the first service tick) or the first `AimOf` | `Player.Aim` / `PlayerStates.AimOf`: needs kiln.playerstate with `aim()` (dev.27) and the camera field in the game's reflection |
| `blocks.affixes` | managed, session | Kiln.Blocks, at the first affix call and every 25 steps | affixes' rolls work (`Affixes.Roll`, `Of`, `Add`, `Set`, `Remove`; [modding/affixes.md](../modding/affixes.md)): follows `saves.instance_data`, with its reason as `needs saves.instance_data: ...` |
| `blocks.affixes.effects` | managed, session | Kiln.Blocks, with the row above | worn and held affixes change stats, and `Affixes.StatOf` reads one: needs `blocks.affixes`, `abilities.programs`, `abilities.natives`, `items.instances`, kiln.assets, kiln.spawn with `spawn.destroy` (a stat change ends when its entity is destroyed: none starts without it), and a server world whose Impact program registry has taken Kiln's four affix programs (pending without a world, and until it has) |
| `saves.instance_data.sync` | managed, static | Kiln.Saves, at its start | item data on players' games (`InstanceKeyOptions.Sync`, `InstanceData.Synced`): the channel `kiln:instance` can open in this process, which needs kiln.net (`service-missing`, or kiln.net's own reason, without it). On a server it says the values are sent; in a game that they can be received |

| `world.tracked_writes` | managed, session | Kiln.World, right before the mods load, then every 25 steps | `World.Write`, `World.Update`, `World.WriteField`, `World.WriteAt` work: kiln.world v3 with the game's tracked write path (`world.programs`, `world.read_other`, `world.names`, and `items.fuel`'s symbols; `symbol-missing` naming the first that is off, `service-older` for a kiln.world without v3). Pending while the write program waits for a server world; `faulted` when the game did not take the program or one of the game's calls faulted. Available with the note "no loaded mod lists a component" when nothing is listed: every write then answers `NotDeclared` |
| `world.component_bits` | sigdb feature | core | the game's per-component save and replication bits can be read (`WorldWriteResult.SaveTracked`, `.ReplicationTracked`, `World.WriteInfo`); off: those answers are null, the writes work the same |
| `world.ecs_read` | native, static | kiln.world, in its `on_ready` | task-thread entity and component reads (`entity_open`, `ecs_snapshot`, `ecs_query` and the other ECS-read members, stable-copy and frame-quiet; [world.md](world.md#world-reads-from-task-threads)). Available on the exact builds (dedicated server 26430253, client 26430625) when the profile's entity table agrees with kiln.world's own resolution on the running exe; the text names the profile. Unavailable, `symbol-missing`, on every other build, the text naming the check that failed (no exact profile, no `kiln.internal.engine`, an incomplete read section, or the field the profile and kiln.world disagree on); `ecs_context_snapshot` still answers |
| `world.ecs_write` | native, static | kiln.world, in its `on_ready` | task-thread compare-exchange writes of untracked plain server-world components (`ecs_write_policy`, `ecs_compare_exchange`, `ecs_write_subscribe`; [world.md](world.md#world-writes-from-task-threads)). Available when the reads are, the exact profile's write gate is enabled and locates the change-tracking bits, and kiln.types is here; the text names the profile. Unavailable, `symbol-missing`, otherwise, the text naming the check that failed; `ecs_write_policy` then answers unavailable |

They exist only once their service has started, after the manifests were checked: check `Capabilities.Is` at run time
(`world.component_bits` is a sigdb feature and can be named in a manifest's `features`).

## Rows added in dev.27 (the contract in every service)
One table for the services that took up the contract in dev.27. A managed row of a service's own is reported at the
service's start, the first service tick (each service with such a row has a startup type in `ServiceTick.AutoStart`),
so a mod's `Capabilities.Is` in its first tick sees it without having called the service; again after the registry was
reset; a session row is refreshed on the service tick. A sigdb feature a service lowers because its table is missing
is lowered at the service's start where it has one, else at its first use (Kiln.Build, Kiln.Creatures: the core's
`kiln.<service>` row says the table is missing from the start). Only the core's own claim is lowered (an available
row from sigdb): with no Kiln core (a unit test, the test kit) a feature has no row, and a test kit's "switched on by
the test" stays the test's. A row says "available" exactly when the call it stands for works: one
that needs a hook says so only while the hook is in. When a service's table is missing, the service lowers the sigdb features it serves, so
`Capabilities.Why` names the missing service and not a feature that resolved for nobody. A service whose table is
there but one of whose hooks failed lowers the features that hook carries (`faulted`): kiln.ui's hook on the game's UI
frame (`ui.screens`, `ui.main_menu`, `ui.system_menu`, `ui.crafting`), kiln.render's camera hook (`render.camera`),
kiln.knowledge's parts by its status (`quests.knowledge`, `changes`, `journal`, `npc`, `dialogue`, `client_input`;
`quests.changes` also when the game's knowledge apply faulted later, read every 25 steps). A session row's refresh asks
for a service the core does not have again, so a table published late is found.

A table published late, one rule for every service (dev.27). A service found missing while the core is there (its
`kiln.<service>` row says `service-missing`) is looked for again at the service tick's refresh, every 25 steps. When its
table is there: the features lowered for it are taken back (the core's sigdb row stands again), a wrapper that
remembered the miss forgets it (its next call finds the table: Kiln.Players' players, progression, player states and
characters, Kiln.Quests' knowledge, Kiln.Saves and its links to kiln.spawn and kiln.players, Kiln.World and its
tracked writes, Kiln.Skills, Kiln.Blocks' factions), a listener that waited for it starts (Kiln.Combat's hit listener,
Kiln.Gameplay's), and every started service reports its rows again. A new registry binding (a reset, the test kit's
next harness) gets every started service's rows again at its first service tick. A service whose wrapper asks again
on every call (Kiln.Network, Kiln.Rules) has no miss to forget and gets its lowered features back. Known limits: these
keep what their first look-up found for the process and are not wired to the rule yet: Kiln.Hooks, Kiln.Build,
Kiln.Content's link to kiln.assets for its programs, Kiln.Ui's craft-menu and render tables (a `Lazy`); kiln.worldtables
merges the mods' blocks at boot, so a table published after it would have missed the merge anyway. Without a Kiln core
(a unit test, a tool) nothing is published later and nothing is looked for.

| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `spawn.components` | managed, static | Kiln.Spawn | this kiln.spawn takes `SpawnOptions.Components` (`service-older` otherwise) |
| `spawn.world` | managed, session | Kiln.Spawn, every 25 steps | the server world runs, so entities can be created and the tracked list cleaned up (`Spawn.WorldRunning`); pending while the save loads and after a hosted world closed; unavailable without kiln.spawn |
| `spawn.create`, `spawn.destroy`, `spawn.entities`, `spawn.templates` | sigdb features | core; Kiln.Spawn lowers them when kiln.spawn is not loaded | creating entities, `Spawn.Destroy`, the entity scan, the game's template list |
| `settings.ready` | managed, session | Kiln.Settings, every 25 steps | the live game settings were found (`Settings.Ready`): pending until the end of the server's first world step; unavailable without kiln.settings. The members still throw `InvalidOperationException` before that, as they always did |
| `settings.live`, `settings.world`, `settings.resend`, `settings.daytime` | sigdb features | core; Kiln.Settings lowers them when kiln.settings is not loaded | the live settings, the world's own values, the resend to players, the day/night clock |
| `assets.textures` | managed, static | Kiln.Assets | texture overrides (`Assets.OverrideTexture`): kiln.assets is here, this is a game client (`wrong-side` on a server), and its resource and content hooks are in (`faulted` with kiln.assets' reason otherwise) |
| `assets.resources`, `assets.content`, `assets.ui_icons` | sigdb features | core; Kiln.Assets lowers them when kiln.assets is not loaded, and each one whose hooks are off (`faulted`, kiln.assets' `status` reason) | resources, content blobs, item icons (game client) |
| `chat.send`, `chat.receive`, `chat.players` | sigdb features | core; Kiln.Chat lowers them when kiln.chat is not loaded | server lines, players' typed lines, the chat's player list |
| `audio.relay` | managed, session | Kiln.Audio, at its start (the first service tick) and every 25 steps | server side: a sound, track or voice line reaches someone (`Sound.Available`, `Voiceover.Available` there): a Kiln client with the `kiln:audio` channel is connected, or this host's own client plays. Pending while nobody listens; unavailable without the channel (`needs kiln.net`); `wrong-side` in a client that only joined |
| `vfx.relay` | managed, session | Kiln.Vfx, likewise | the same for `Vfx.Available` and the `kiln:vfx` channel |
| `screen.relay` | managed, session | Kiln.Vfx, likewise | server side: the `kiln:screen` channel is open (`ScreenEffects.Available` there) |
| `gameplay.entity_events` | native, static API alias | kiln.gameplay (managed fallback on older tables) | lifecycle demand can be accepted, including pending (`Gameplay.EntityEventsAvailable`); see `gameplay.entities_hooks` for delivery state |
| `gameplay.spawn_replace` | native, static API alias | kiln.gameplay (managed fallback on older tables) | natural-spawn replacement registration is supported; see `gameplay.spawn_hooks` for activation/refusal |
| `gameplay.*` (ten), `build.*` (five), `skills.functions`, `skills.login_guard`, `skills.shadow`, `skills.tree_load` | sigdb features | core; Kiln.Gameplay, Kiln.Build and Kiln.Skills lower theirs when their service is not loaded | what kiln.gameplay, kiln.build and kiln.skills serve |
| `content.references` | managed, static | Kiln.Content | this kiln.content sets references (`ContentEntry.SetRef`, `SetPerks`; table v4) |
| `content.ready` | managed, session | Kiln.Content, at its start and every 25 steps | the registries are loaded and the data packs applied (`Content.Ready`); unavailable without kiln.content |
| `content.*` (eight, server side), `gamedata.template_clones`, `gamedata.buff_lookup`, `creatures.*` (ten) | sigdb features | core; Kiln.Content and Kiln.Creatures lower theirs when their service is not loaded | what kiln.content, kiln.gamedata and kiln.creatures serve |
| `quests.*` (seven: `quests.npc_pages` since 0.2.0-dev.29), `ui.*` (eight, game client), `render.camera` | sigdb features | core; Kiln.Quests and Kiln.Ui lower theirs when kiln.knowledge, kiln.ui or kiln.render is not loaded | what kiln.knowledge, kiln.ui and kiln.render serve |
| `combat.attributes` | managed, session | Kiln.Combat, at its start and every 25 steps | `Attributes` reads and writes the game's attributes: kiln.combat has its attribute functions (`service-older` otherwise) and the game's attribute resource was read, and a server world with its registry is there now (pending until a server world runs and again after it closed; pending for good on a build whose resource Kiln does not understand) |
| `combat.actor_states`, `combat.attr_heals`, `combat.deaths`, `combat.falls`, `combat.health`, `combat.hits`, `combat.regen` | sigdb features | core; Kiln.Combat lowers them when kiln.combat is not loaded | what kiln.combat serves |
| `abilities.attributes`, `abilities.instances`, `abilities.natives`, `abilities.programs`, `abilities.sequences` | sigdb features | core; Kiln.Abilities lowers them when kiln.abilities is not loaded (at its start, the first service tick, with `abilities.buffs` and `abilities.buff_list`) | what kiln.abilities serves |
| `time.speed` | managed, session | Kiln.Time, at its start (the first service tick), refreshed every 25 steps | the clock's speed can be held now (`Time.SetSpeed`, `Time.SetCycle`): needs kiln.time with its scale functions (`service-older` without) and `time.scale`; pending until the server world's clock is there (a client in its menu, a server before its world runs: `SetSpeed` throws `InvalidOperationException` then, as before). `Time.CanSetSpeed` stays the static part |
| `time.read`, `time.set`, `time.scale`, `time.sync_probe`, `time.weather`, `time.weather_choose` | sigdb features | core; Kiln.Time lowers them when kiln.time is not loaded | what kiln.time serves |
| `players.positions` | managed, session | Kiln.Players, at its start (the first service tick) and every 25 steps | `Players.PositionOf` works now: kiln.players reads positions in a running server world (pending without one, `faulted` when the world runs and it still cannot; unavailable without kiln.players) |
| `players.records`, `players.admin`, `players.singleton_map`; `players.progression`, `players.skills`, `players.skill_knowledge`, `players.progression_events`; `players.reflection`, `players.action_cancel`, `players.sequence_names`, `players.client_action_hold` | sigdb features | core; Kiln.Players lowers them at its start when kiln.players, kiln.progression or kiln.playerstate (in that order) is not loaded | |
| `net.client_guard`, `net.gate`, `net.kick`, `net.players`, `net.transport`, `net.withhold` | sigdb features | core; Kiln.Network lowers them at its start when kiln.net is not loaded | |
| `saves.load`, `saves.reasons`, `saves.request`, `saves.write` | sigdb features | core; Kiln.Saves lowers them at its start (its first use) when kiln.saves is not loaded | |
| `rules.consume` ... `rules.zone` (sixteen) | sigdb features | core; Kiln.Rules lowers them at its start when kiln.rules is not loaded | |
| `world.component_bits` ... `world.write` (twelve) | sigdb features | core; Kiln.World lowers them at its first use when kiln.world is not loaded | |
| `items.pide_options` | sigdb feature | core; Kiln.Items lowers it with its other rows when kiln.items is not loaded (dev.27; before, it kept the core's verdict) | `Gear.Unlocked`, `Gear.GemSlot` at creation |
| `gems.sockets` (server) | sigdb feature (server.d/gems-sockets.json: items.pide_options' symbols + `items/item.gem_override`) | core; kiln.items refuses its v7 options (`KILN_ERR_UNAVAILABLE`) without it | sockets on other gear: `Gems.AllowSockets`' server part, `Gear.GemSlot` on non-weapons, `Gems.AddSocket`, rolls, `gemSlotOverride` on any gear ([guide](gems-sockets.md)) |

## Rules
- Each owner holds at most one report per name. A reader sees the worst report for the name: unavailable beats
  pending, pending beats available. So a service can say a feature is off although its symbols resolved (its hook
  failed), and nobody can report a capability available whose symbols are missing.
- Reports go with their owner: when an owner is revoked, its reports go, and a row with no report left is removed.
- A name that was ever reported pending carries the session flag from then on (`SessionDependent`, `"session": true`
  in the file). A manifest may not require it. Names the core reports itself (sigdb features, `kiln.<service>`) are
  never pending: `report()` refuses that.
- `report()` takes names of 1 to 96 bytes, cuts the text at 512 bytes, and allows 256 reports per owner.
- Every function may be called on any thread. Subscribers run after the registry's lock is released, on the thread
  that caused the change; one that faults is removed.

## C (native plugins)
```c
const KilnCapsApi* caps = api->service((KilnStr){"kiln.caps", 9}, 1);
KilnCapRow row = { .size = sizeof row };
if (caps && caps->find((KilnStr){"items.fuel", 10}, &row) == KILN_OK && row.state == KILN_CAP_AVAILABLE) { /* ... */ }
caps->report(owner, (KilnStr){"myplugin.flight", 15}, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED,
             (KilnStr){"the flight hook did not go in", 29});
```
`count`, `at`, `find`, `report`, `subscribe`, `unsubscribe`, `generation`. Strings in a row filled by `at` or `find`
stay valid until the same thread's next `at` or `find`: copy what you keep. Report a session row as pending before
the mods load (in `kiln_plugin_main` or `on_ready`), not only once a world is there.

## The file: `kiln/state/capabilities.json`
Written by the core alone: when boot completes (also in safe idle), then at most once a second after a change, and
once more at a normal exit. A failed write is retried each second. It holds the format, Kiln's version, the side, the
exe's SHA-256 and whether its build is verified, whether hooks are on, the safe-idle sentence, the generation and
every row. Nothing in the launcher reads it yet; `tools/docs-site/status-tables.py` makes the docs' status tables from it.

## Limits
- **A managed service's own rows exist only after that service was first used**, usually after the manifests were
  checked. A manifest that requires such a `<service>.<thing>` name is refused as "no capability of this name is
  registered", with a hint to check `Capabilities.Is` at run time instead. Sigdb features and `kiln.<service>` rows
  are not affected.
- A tick hook that faults at run time: its rows follow within about a second (the writer refreshes them while hooks
  are on). Shown with host stubs, never seen in a running game.

## Tested
- Host, no game: `test-caps.sh` (with one pattern broken on the test exe, that feature reads
  `symbol root: root.find[0]: 0 matches` and the others stay available).
- Lab (dedicated server under Wine), `test-caps-lab.sh --with-mods`: the file validates against the schema, a broken
  pattern of `gameplay/CraftRecipe` makes `gameplay.craft` unavailable with the symbol named, and a mod requiring it
  is refused with `needs feature gameplay.craft, unavailable: symbol gameplay/CraftRecipe: … [symbol-missing]` in
  `kiln/state/mods.json` and kiln.log. `--safe-idle`: every feature row says `policy`.
- Not run: a game client, native Windows.

## Rows added in dev.28
Rows a dev.28 package adds, by service. The "Held by" column names no holds in this release:
all holds were removed in 0.2.0-dev.29. Guided checks confirm behavior; they no longer open these rows.

The seven `ui.values.*` / `ui.slots.inspect` / `ui.gemforge.inspect` rows below are client-only and require measured client build 26430625; a game update leaves them off until Kiln is updated. Hooks install on first use, including a slot read. Tool-wear hooks install on the first valid rule, rather than at startup. `mods.boot.vfx` is not in this build on a client (its row reads unavailable, `service-missing`): VFX and material files are built at start.

| Row | Kind | Reported by | Held by | What it says |
|---|---|---|---|---|
| `blocks.kinds` | managed | Kiln.Blocks | - | A mod can register a block kind (`Blocks.RegisterKind`). Off where blocks have nothing to compile to |
| `blocks.compile` | managed | Kiln.Blocks | - | A block of a registered kind compiles to a frozen plan (`Blocks.Compile`, `Blocks.Application`) |
| `blocks.apply` | managed | Kiln.Blocks | - | Plans are applied (`Blocks.Apply`, `Blocks.Define` with a block of an external kind). Needs a Kiln that can revoke a plan's owner |
| `blocks.apply.item`, `.weapon`, `.spell`, `.status`, `.effect` | managed | Kiln.Blocks | - | A plan whose primary is this built-in block is applied |
| `types.impact_names` | service row | kiln.types at its start | not held | the Impact-name field of the game's type table validated on this build: `GameType.ImpactName`, `GameTypes.ByImpactName`, `FindAllByImpactName` ([types.md](types.md#impact-names-since-020-dev28)); off with the counts that failed, or missing with a kiln.types older than 0.2.0-dev.28 |
| `world.transform_scale` | managed row | Kiln.World, right before the mods load (pending while the game's type table is empty) | — | `World.SetTransformScale` ([world.md](world.md#transform-scale)): the game's type table gives `CurrentTransform.transform.scale` as three contiguous float32; the text names the place, or the shape found when it is off |
| `assets.blob_catalog` | managed row | Kiln.Assets at its start | — | the content blob catalog (`Assets.OpenBlobCatalog`, [assets.md](assets.md#the-content-blob-catalog)): kiln.assets' table has it and the content hooks are on; `service-older` with an older kiln.assets |
| `repl.observe` | sigdb feature, static | core (sigdb); `kiln.repl` lowers it (`faulted`) when its send-step hook fails | — | what the server sends each peer can be read (server, and a hosted world's machine 0) |
| `repl.client` | sigdb feature (client exe), static | core (sigdb); `kiln.repl` and `Kiln.Replication` lower it (`service-older`) while kiln.repl has no receive side; `wrong-side` on a server | — | the client receive watch row is open at the gate, but the current native service still reports `service-older` (receive side not built); check `11-repl.receive-watch` remains unconfirmed |
| `repl.loopback` | sigdb feature (server exe), static | core (sigdb); `kiln.repl` and `Kiln.Replication` lower it (`policy`) without kiln.json's policy `any` and kiln.net's lab port; `wrong-side` on a client | — | the loopback harness (`ReplicationLab`, lab servers only) answers |
| `worldtables.budgets` | service row | kiln.worldtables | — | Process material/pool snapshots; the running executable's table layouts must resolve. Counts remain NotReady until boot publication, and unknown pools return NotFound. No custom pools leaves the existing table hash unchanged. |
| `worldtables.material_pools` | service row | kiln.worldtables | — | Data-only custom pools (1..64 building item IDs) for opt-in new blueprints. Requires this executable's pool layout and 16-byte item pool reference; unresolved inputs report unavailable with their own reason. Enabled when resolved; real menu ingestion and client/rejoin proof remain `17-material-pools`. |
| `world.systems` | sigdb feature | sigdb | — | the program scheduler (`world/StepPrograms`, `RunInitPrograms`, `SliceTaskFn`) and the players mask resolved |
| `systems.threads` | service row | kiln.world | — | the server world's players mask can be read (what puts its program slices on the game's workers) |
| `systems.wraps` | service row | kiln.world | — | Before/After/Replace on the game's programs work on this build; unavailable while native SET is loaded |
| `systems.wraps.typed` | service row | kiln.world | — | typed wraps (`Systems.Wrap<T>` with a view of Kiln.Game's `Ecs.Programs`, since 0.2.0-dev.29): reported with `systems.wraps`, its state and reason; each wrap then checks its view's contract against the live descriptor (`game_program_contract`) and refuses one that differs |
| `systems.define` | service row | kiln.world | — | `Systems.Define` (since 0.2.0-dev.29): a mod's own `[Query]` program, reserved at boot from its `kiln.programs.json` and bound by key; follows whether kiln.world's own programs (`world.programs`) hooked this build |
| `world.programs.client` | service row (client) | kiln.world | — | Kiln programs of a mod's own in this player's client world and the prediction storage (`EcsPrograms.TryBind(…, WorldKind.Client / Prediction)`, kiln.world's `register_program_world` / `bind_program_world`, EMF's `EMF_WORLD_CLIENT` programs and `on_event`; [world.md](world.md#client-world-programs-since-020-dev29)), on since 0.2.0-dev.29 and not yet confirmed in a real game (guided checks `programs.client-baseline`, `14-client-world-programs`, `programs.client-joined`, `14-q4`, `programs.client-emf`, `programs.client-visual`, `programs.client-persistence`, `programs.client-rejoin`, `programs.client-delayed-stop`, `programs.client-exit`); requires kiln.world's program hook and table layout, the name maps, `world.lifecycle` and the client world's tick and world events; the text says "waiting for a world" in the menu, "a client world runs" in game, and which guarantee a client world's stop has (`world.stop_drain.client`: "the engine drains the world's jobs first", or a bounded wait only); `wrong-side` on a dedicated server; unavailable (`faulted`) for the rest of the process when a call outlived its client world's stop, was inside it although the engine drained the world's jobs, or a client registration faulted |
| `systems.wraps.client` | service row (client) | kiln.world | — | wraps on the client world's programs (native plugins, and mods through Kiln.Systems with `WrapOptions.World`), on since 0.2.0-dev.29 and not yet confirmed in a real game (guided check `06a-wraps.client`); requires symbols, lifecycle support and no native SET conflict; a native caller must release and drain before freeing callbacks; `wrong-side` on a dedicated server |
| `hooks.typed` | sigdb feature | core | - | kiln.hooks has typed links (`detour_typed`, `call_with_frame`, `fingerprint`): a hook or call of a game function with float, stack and struct-return positions |
| `sig/<symbol>` | sigdb feature | core | - | the symbol has a `sig` descriptor, resolved, and its function's code fits the descriptor on this build (one row per described symbol: `sig/time/SetTimeScale`, `sig/items/GetItemVolume`, ...). Off: the reason names the argument that no longer fits; the symbol itself may still resolve. Check it before a native hook relies on the described signature |
| `items.volume` | sigdb feature | core | - | `items/GetItemVolume` resolved |
| `weather.sample` | sigdb feature | core | - | `weather/SampleAt` resolved (captured from its call in `fuel_setup`) |
| `hooks.owner_cleanup` | sigdb feature | core | - | kiln.hooks has `subscribe_owner_retiring`: a native service is told when an owner it keeps state for is revoked |
| `kiln.queries` | service | core | - | kiln.queries' table is here ([queries.md](queries.md)) |
| `queries.voxel_ray` | sigdb | sigdb; kiln.queries lowers it (faulted) | — | The game's voxel ray: `Queries.RaycastVoxels`. Off for the process after a fault in the ray or a voxel grid that does not match the build's layout |
| `queries.ground` | managed | Kiln.Queries; kiln.queries lowers it on ray failure | — | `Queries.GroundBelow`; needs `queries.voxel_ray` |
| `queries.line_of_sight` | managed | Kiln.Queries; kiln.queries lowers it on ray failure | — | `Queries.VoxelLineOfSight`; needs `queries.voxel_ray`, `kiln.world` and `world.entities` |
| `queries.path` | sigdb | sigdb; kiln.queries lowers it (faulted, program refused) | — | The game's path search: `Queries.RequestPath`, `FindPath`, `CancelPath` |
| `queries.reachability` | managed | Kiln.Queries | — | `Queries.CanReach`; needs `queries.path` |
| `queries.overlap_origins` | managed | Kiln.Queries | — | `Queries.EntitiesInSphere`, `EntitiesInBox`: entity origins from `kiln.world`; needs `world.entities` and `world.find` |
| `kiln.terrain` | managed | Kiln.Terrain | - | The library is loaded ([terrain.md](terrain.md)): `Terrain.Inspect` |
| `terrain.overlays` | managed | Kiln.Terrain | — | `Terrain.Apply`, `Forget`, `Applied`; needs `kiln.build`, `build.read`, `build.write`, `build.persist`, `kiln.saves`, `saves.load`, `saves.write` (actual load/write hooks), actual core `world.lifecycle` stop observation, successful Build subscription and the shared native budget extension. Lifecycle failure reports unavailable. Receipts, new-world proof and legacy history retain normal behavior; without history, 100% matching addressed cells adopts, at least 25% returns NeedsAdoption without writing, below 25% installs with the measured share recorded. More than 75% removed can be refilled |
| `terrain.baked` | managed | Kiln.Terrain | - | Always off in this version: baked terrain regions are not built |
| `terrain.biomes` | managed | Kiln.Terrain | - | Always off in this version: biome changes are not built |
| `blocks.move_followups` | managed | Kiln.Blocks, with the item blocks' rows | — | A weapon's moves with `FollowUps`, applied as one generation: `MoveBlock.FollowUps`, `Blocks.MovesStatus`, `WeaponMovesApplicationChangedEvent`. Both sides; the query on any thread, application and events on the tick. Needs kiln.gamedata (sequence clones, their rules, item data); a move that names a state (by name, GUID or `0x` id) also needs `anim.player_states`. Publication waits for all consuming worlds' sequence registration; a refused definition or sync route does not turn this row off |
| `creature_control` | static (lowered when the broker's world program does not run) | kiln.creature_control | not held | the creature control broker takes new leases: its layout guard matched through kiln.types, its world program is registered (kiln.world with `write_other`), kiln.creatures has the AI switch ([creature-control.md](creature-control.md)); the table row `kiln.creature_control` serves queries, waits and releases |
| `mods.boot` | sigdb feature (both exes), static | core (sigdb: `assets/ResourceInit`, `content/ItemRegistryUpdate`, `RegisterSingleton`); kiln.boot lowers it (`service-missing`) when a collector did not register in a boot process | — | Mod.OnBoot's phase can run in this process (on a game client it also needs `mods.boot.client`) ([../modding/onboot.md](../modding/onboot.md)); off: boot mods are refused (`RestartRequiredBoot`) |
| `mods.boot.phase` | service row (session) | kiln.boot | - | Informational: `available` while a boot mod's OnBoot frame is open (the text names it), `pending` while the generation collects, `unavailable` (`policy`) once the phase is over or when this process has none (the text says why: safe idle, .NET off, no boot mod, or a configured client hold; this release holds none) |
| `mods.boot.game_data` | service row | kiln.boot, at its start | - | `BootContext.GameData.Read` has a game-data snapshot of exactly this game build ([../modding/boot-gamedata.md](../modding/boot-gamedata.md)); `service-missing` with the command that makes one otherwise, `policy` in safe idle |
| `mods.boot.client` | service row | kiln.boot, at its start | — | Boot mods on a game client (the hosted world and the client); `wrong-side` on a dedicated server, whose boot mods are `mods.boot` |
| `mods.boot.vfx` | service row | kiln.boot, at its start | — | Staging VFX and materials into a client's boot generation. Not in this build: `unavailable` (`service-missing`) on a client, where VFX and material files are built at start; `wrong-side` on a dedicated server (it renders none: an empty plan) |
| `mods.boot.resource_patches` | native, static | kiln.content, at its start (and again with a note when a write of this start was not applied) | — | Fields of a game resource stated as resource patches (a boot mod's `OnBoot` or its blocks files) are written before the game reads its resources. Off without a boot phase in this process, and where kiln.assets' resources are off |
| `kiln.ui.values` | service row | core | — | the in-core service is here (client only; on a server its init says no) |
| `ui.values.detail` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`) when its hook cannot be installed | — | providers can replace the item detail view's number, bar and label |
| `ui.values.slot` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`) | — | providers can replace the inventory slot bar |
| `ui.values.hud` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`) | — | providers can replace the local player's health, stamina and mana pairs; also the player-UI stage 09 reads |
| `ui.values.held` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`) | — | providers can replace the held item's water indicator |
| `ui.values.tooltip_lines` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`); Kiln.Ui requires the `kiln.ui` window backend | — | tooltip lines and Kiln's Item details window |
| `ui.slots.inspect` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`); needs `ui.screens` | — | the slots of each completed UI frame (UI units; pixels also need the verified mapping) |
| `ui.gemforge.inspect` | sigdb feature (client), static | core (sigdb); kiln.ui.values lowers it (`faulted`); needs `ui.screens` and the item catalog | — | read-only returned GEMS list for the local player; [API](../ui-gemforge.md); real-client acceptance pending |
| `gems.sockets` (client) | sigdb feature (client.d/gems-sockets.json), static | core (sigdb); kiln.ui.values refuses `gemforge_sockets` without it | — | the Gem Forge's WEAPONS list and socketed rows also show other gear that has a socket (`Gems.AllowSockets` on the client); [guide](gems-sockets.md); real-client acceptance pending |
| `net.gate.hold_all` | service row, static | Kiln.Network, at its start | — | kiln.net can hold a Kiln client that already proved itself (gate mode "hold all", kiln.net since 0.2.0-dev.28) and the gate hook resolved: `NetAdmission.HoldJoins`, `HoldPeer` and `OnJoin` work ([network.md](../network.md#admission)). Off: needs `net.gate`, or this kiln.net is older; `wrong-side` on a client that only joins |
| `net.admission` | service row, static | Kiln.Network, at its start | — | the admission controller decides joins in this process: a server role, kiln.net and `net.gate`. Services that register requirements (mandatory protocols, holds, vanilla conditions) check it; `wrong-side` on a client that only joins |
| `net.disconnect` | sigdb feature (dedicated server), static | core (sigdb); Kiln.Network lowers it when kiln.net is missing | — | kiln.net can end a refused client the game's login failure cannot reach (not on the build, or it faulted) with the game's own session kick, which closes the connection about a second later ([network.md](../network.md#admission)). Off (a hosted world, or the symbol did not resolve): such a client stays connected but blocked, with no world data, until it leaves |
| `items.tool_wear_debit` | sigdb feature | core (sigdb); kiln-items.dll lowers it (its guards, a hook fault: `faulted`) | — | `Items.SetToolWearPerDebit`, `ToolBlock.WearPerDebit`: a cost per game debit of an axe or pickaxe ([items.md](items.md#tool-wear-per-debit-since-020-dev28)); off on a build whose frames are not approved |

## Rows added in dev.29
Rows a dev.29 package adds, by service (as in "Rows added in dev.28" above). None is held: the "Unconfirmed" column
names the guided check a real game has not passed yet for a row that is on.

| Row | Kind | Reported by | Unconfirmed | What it says |
|---|---|---|---|---|
| `abilities.natives.attach` | managed, static | Kiln.Abilities, with `abilities.natives.sync` (decided before the mods' OnLoad) | `06c-natives.joined` (lab-proven on a dedicated server without a player) | handlers attached to a vanilla native's calls in one program at one site (`Abilities.Natives.Attach`): needs what `abilities.natives.sync` needs. Available in OnLoad on a server: an attachment made there is accepted and `Pending` until the world's programs are in, then `Active` or `Off` (its own state, not the row's) |
| `abilities.natives.ecs` | managed, session | Kiln.Abilities, with `abilities.natives.sync` | `06c-impact-damage` (lab-proven on a dedicated server without a player) | a native's call reads and writes other entities through impact_simulation's own accessors (`NativeContext.TryRead`, `TryWrite`, `Root`): its condition is that impact_simulation runs serially on the server world's thread (the world's pin byte, sigdb feature `abilities.ecs_pin`, set; every call on that thread): `pending` (`session`) until the first Kiln native call of a server world checked its iterator against kiln.world's view and that condition, `faulted` for the rest of a world where either fails (the pin cleared, a call on a worker, a foreign access that faulted; the natives still run), `symbol-missing` where sigdb does not resolve the pin, `service-older` without a dev.29 kiln.world. A faulted write may already have changed state; the write-event queue is bounded |
| `abilities.natives.sync` | managed, static | Kiln.Abilities, at its start (right before the mods' OnLoad, since 0.2.0-dev.29; again at the first service tick) and about once a second | `06c-natives.joined` (lab-proven on a dedicated server without a player) | synchronous natives (`Abilities.Natives.RegisterSync`): needs a dev.29 kiln.abilities (`service-older` without), its RegisterNative hook (`faulted`) and the sigdb row `abilities.natives` (its reason when off: a game client has no signatures for it, so the row is `symbol-missing` there) |
| `kiln.decisions` | service | core; kiln-decisions.dll lowers it (`service-missing`) without `tick.wait_jobs`, kiln.window_clock or kiln.world, and (`faulted`) with `events.death`, `events.pickup`, `events.equip` and `decisions.native_guards` when one of its window calls or hooks faulted (the service shuts down for the process) | - | the decision broker is here ([decisions.md](decisions.md)) |
| `kiln.window_clock` | service (in-core) | core | - | the step's phase, world epoch and process session ([decisions.md](decisions.md)) |
| `events.death` | sigdb feature with session installation | core (sigdb); kiln.decisions lowers it (`faulted`, `service-missing`, `symbol-missing`) when its checked hook installation fails (pending until safe Pre), kiln.combat is missing or the world layout constants are unknown | `07-events.decision-rollback` | `EntityDyingEvent`: a death from health can be refused (on since 0.2.0-dev.29, not yet confirmed in a real game) |
| `events.pickup` | sigdb feature with session installation | core (sigdb); kiln.decisions lowers it (`faulted`, `symbol-missing`) when its checked hook installation fails (pending until safe Pre) or the world layout constants are unknown | `07-events.decision-rollback` | `ItemPickingUpEvent`, `Decisions.ReevaluatePickup`: a walk-over pickup can be refused (on since 0.2.0-dev.29, not yet confirmed in a real game) |
| `events.equip` | sigdb feature, static | core (sigdb: the game's EquipItem, UnequipItem); Kiln.Players lowers it (`service-older`, `symbol-missing`) when kiln.playerstate cannot hold an equipment input alone | - | `PlayerEquippingEvent`: one verdict with `PlayerClientActionEvent` for a player's equipment input |
| `decisions.native_guards` | service row | kiln.decisions at its start; lowered with `events.death` when the death hook could not go in | - (follows `events.death`'s hook, which goes in with the first guard) | `set_death_guard`: a native plugin can keep an entity's death from running (19d pets) |
| `events.build.pieces` | service row | kiln.decisions at its start | - | always `unavailable` in 0.2.0-dev.29: player build pieces are not cancelable yet (07 S0/S9) |
| `events.build.terraform` | service row | kiln.decisions at its start | - | always `unavailable`: managed terraform cancel is not part of V1 |
| `events.component_net` | sigdb feature (both executables: `GetComponent`, `WaitWorldJobs`), static | core (sigdb); kiln-decisions.dll reports it at its start: `service-missing` without kiln.decisions, kiln.window_clock or kiln.world, `symbol-missing` when the feature did not resolve on this executable, `faulted` when a window call of kiln.component_changes faulted (for the process) | `07-watch.hosted` | `ComponentChanges.Watch`, `Resume` and the three events (`ComponentValueChangedEvent`, `AttributeValueChangedEvent`, `ComponentWatchStateChangedEvent`): named component watches of the server world ([decisions.md](decisions.md#named-component-watches-componentchanges-since-020-dev29)). Its text says what of it is off on this build (scope Players without kiln.players, typed values without kiln.combat v2 or kiln.items' fuel) and, on a game client, that it works only while the client hosts the world (a joined client has no server world: `Watch` answers `Ok = false`). Lab-proven on a dedicated server without a player; never run in a game client |
| `kiln.component_changes` | service | core; kiln-decisions.dll lowers it with `events.component_net` | `07-watch.hosted` | the component watches' service is here (kiln_component_changes.h) |
| `gems.craft` | sigdb feature (`server.d/gems-forge.json`; the client's hosted copy composes it unchanged, `client.d/gems-forge.json`) | core (sigdb); kiln-items.dll lowers it (`symbol-missing`) when the handler's layout constants do not check out or `equipment_actions`' declaration in the game's program directory differs from the iterator Kiln builds, (`service-missing`) without kiln.world or with one too old to show `equipment_actions`' declaration, (`faulted`) when the program is refused, the handler hook cannot be installed, or a forge action faults (off until restart); Kiln.Abilities lowers it at its start (`service-missing`) where kiln.gemcraft is not here (an older kiln-items.dll, the test kit) and refuses its events while kiln.gemcraft is not here | gem wave G3 client check: a player's forge action held and canceled, and the forge screen after a code action | `Gems.Socket`, `Unsocket`, `Upgrade`, `Recycle` with the game's costs (or a free / replaced cost); `GemCraftingEvent` (players' forge actions, cancelable) and `GemCraftedEvent`; `Gems.ReturnSocketedGems`' forgeless return ([gems-forge.md](gems-forge.md)) |
| `items.equip_sets` | sigdb feature (`server.d/dev29-10-equipment-sets.json`, used by the client unchanged: the game's equip functions and the commit's own rollback guards, captured from `items/InventoryCommit`) | core (sigdb); Kiln.Items lowers it at its start when its table is older than v6 (`service-older`), `items.equip` or `items.instances` is off (their reason), or kiln.items says sets cannot run (kiln.decisions or kiln.window_clock missing, this build's component layouts differ from what the snapshot reads, `faulted`); off until the server, or the hosting player's game, restarts after an invariant failure or a fault inside a set's transaction (`faulted`), and for the session like `items.equip` on a game client with no server world (`session`) | `10-items.equip-set` (and `10-items.equip-set.observer` with a second player) | `Items.CaptureSet`, `PlanSet`, `EquipSet`: a player's 20 equipment slots changed in one transaction with one decision (on since 0.2.0-dev.29, not yet confirmed in a real game) |
| `events.component_writes.exact` | service row | kiln.decisions at its start | - | reserved: always `unavailable`, no feed of every component store is promised (the watches report net changes, and only for watched pairs) |
| `gems.payers` | sigdb feature (`server.d/gems-forge.json`, since 0.2.0-dev.29.15; the client's hosted copy composes it) | core (sigdb); kiln-items.dll lowers it (`symbol-missing`) when its call sites did not resolve or are not the measured calls when Kiln goes to hook them, (`faulted`) when a mid hook cannot be installed; Kiln.Abilities lowers it (`service-older`) with a kiln-items.dll before 0.2.0-dev.29.15 | lab-proven on a dedicated server without a player; base storage with a real player is unconfirmed | `GemCraftOptions.PayFrom` / `ReturnTo` and an offer's payer other than the player: Kiln moves the gem handler's own payment, unslot move and Recycle give ([gems-forge.md](gems-forge.md#who-pays-and-where-things-go-since-020-dev2915)) |
| `gems.sources` | managed, session | Kiln.Abilities, with the gem rows (about once a second) | issue 61 client check: seen in a real game (bisdat, 2026-11-17, a hosted world; [gems-forge.md](gems-forge.md#seen-in-a-real-game-020-dev2915)); lab-proven on a dedicated server without a player | `Gems.OfferChestGems`, `WithdrawChestGems`, `GemForgeOpenedEvent`: needs `gems.craft` (its reason), kiln.gemcraft's 0.2.0-dev.29.15 part (`service-older`) and kiln.net (`service-missing`); `pending` (`session`) until the server world's program runs |
| `ui.gemforge.sources` | sigdb feature (client, `client.d/gemforge.json`, since 0.2.0-dev.29.15) | core (sigdb); kiln.ui.values lowers it (`faulted`) for the process when a site's bytes are not the measured instructions or a hook cannot go in; Kiln.Ui lowers it (`service-older`) with an older kiln.ui.values | issue 61 client check: seen in a real game (bisdat, 2026-11-17, a hosted world: rows, socket, upgrade, recycle, withdraw); a remote client on a dedicated server not yet | chest gems a server offered, in the Gem Forge's GEMS grid ([../ui-gemforge.md](../ui-gemforge.md#chest-gems-in-the-gems-grid)); its hooks go in only when a server sent rows |
| `events.thread_report`, `events.build`, `events.observe_marks` | sigdb features, static | core (sigdb) | - | dependency rows of chapter 07's signatures (the players mask, the building primitives, the mark functions); they claim no capability of their own |
| `gamedata.buff_budget` | service row, static | kiln.gamedata with its table (kiln-content.dll); Kiln.Content says `service-missing` / `service-older` without it | `17-buffs-client` | `Content.Buffs.GetBudget()` answers ([gamedata.md](gamedata.md)). Not off for a world that is not there: the budget answers `NotReady` then |
| `gamedata.buff_registration` | service row, static | kiln.gamedata with its table; lowered (`faulted`) when no room at all could be reserved or the world's buff collection is not readable | `17-buffs-client` | `Content.Buffs.Define(clone, options)` and `GetRegistration` answer; guided check `17-buffs-client` for what a player sees |
| `world.program_budget` | service row, static | kiln.world with its table kiln.world.budget (kiln-world.dll); Kiln.World says `service-missing` / `service-older` without it | - (the client kind: no guided check yet; it answers NotReady where programs are off) | `World.GetProgramBudget` answers ([world.md](world.md#program-budget)); its readiness says when a world is not there or not read yet. The 64 program slots themselves have no row: `KilnSlots.Expanded` says they are installed |
| `npcs.roster_read` | service row, static | kiln.knowledge (kiln-quests.dll) at its start: unavailable (`symbol-missing`) when the NPC registry's layout did not resolve (`quests.npc`); Kiln.Quests says `service-missing` / `service-older` without the table | - | `NativeQuests.GetServerRosterWorld`, `GetUnlock` answer from the server world's snapshots ([npcs.md](npcs.md#the-rosters-size-since-020-dev29)); `NoWorld` / `NotReady` are answers, not a missing capability |
| `npcs.client_roster_read` | service row, static | kiln.knowledge at its start: `wrong-side` on a dedicated server; otherwise as `npcs.roster_read` | `17-roster-client` | `NativeQuests.GetClientRosterWorld`, `GetClientUnlock`: the client world's own registry (on since 0.2.0-dev.29, not yet confirmed in a real game) |
| `npcs.roster_budget` | service row, static | kiln.knowledge at its start, as `npcs.roster_read` | - | `NativeQuests.GetRosterBudget`: capacity 64 or 128 (`Expanded`), game's, Kiln's and other entries |
| `npcs.expanded_roster` | service row | kiln.knowledge at its start, again when the boot build installs the page: available "installed" (the roster holds 128) or "ready" (it installs at the start when a mod's roster needs more than 64 entries; nothing is hooked before); unavailable `symbol-missing` (`quests.npc_pages` did not resolve, or feature `singletons` is off), `faulted` (a hook could not go in, the lab switch `kiln/control/roster.fail-install`, or the functions disagree with the layout), `service-older` (kiln.hooks has no checked installation), `policy` (the lab's negative control `kiln/control/roster.no-pages`, only with `kiln/control/roster.lab`). A roster past what it allows stops the start (`KILN_KNOW_ROSTER_EXIT_CODE`) | `17-roster-client` | the roster's maximum is 128 on this side (on since 0.2.0-dev.29; the client's directory, summon and beds with entries 0 and 64 not yet confirmed in a real game) |
| `gamedata.expanded_buff_room` | service row, session | kiln.gamedata at the first world | `17-buffs-client` | pending until the first world loads; available when the room of 4,096 buff clones was reserved next to the game's collection; unavailable (`faulted`) with the text "… the room is 1024" on the fallback |
| `vfx.budget` | service row, static | kiln.vfx with its table (kiln-vfx.dll, game client), before the mods load; lowered (`faulted`) when the guard turns off; Kiln.Vfx says `service-missing` (needs kiln.vfx: a dedicated server has no VFX) / `service-older` without the table | `17-vfx-budget.before` / `17-vfx-budget.after` | `Vfx.ResourceBudget` answers ([vfx.md](vfx.md#the-resource-budget-since-020-dev29)): available when this game build's VFX count limit can be read (`symbol-missing` naming the `caps/vfx.*` constant that did not resolve, or the layout that does not check out). Not off before a world: the budget answers `NotReady` until kiln.vfx read the game's table |
| `blocks.vfx_registration` | service row, static | kiln.vfx with its table; lowered (`faulted`) when Kiln's VFX are not built this session (no kiln.assets, the particle layouts changed, the build failed) or the guard turns off; Kiln.Vfx and Kiln.Blocks say `service-missing` / `service-older` without the table | `17-vfx-budget.before` / `17-vfx-budget.after` | `Blocks.Define(name, VfxBlock, options)` and `Blocks.GetVfxRegistration` answer: as `vfx.budget`, and a resource's bytes in the game's table can be compared with what Kiln served (`symbol-missing` naming vfx.play's constants or `caps/vfx.table_capacity_offset` otherwise) |
| `stations.read` | sigdb feature (`server.d/stations.json`, used by the client's hosted world unchanged) | core (sigdb); kiln-items.dll lowers it (`symbol-missing`) when a layout constant does not check out, (`service-missing`) without kiln.world; Kiln.Blocks lowers the four stations rows at its first use (`service-missing`) where kiln.stations is not here (an older kiln-items.dll, the test kit) | C1, C3 | `Bases.*`, `Stations.Read`, `All`, `InBase`, `Nearest`, `Recipe`, `RecipesOf`, `Categories`, `ResolveInputs`, `Plan`; with `items.move` also `Collect`, `CollectToBase` ([stations.md](stations.md)); off on a joined client (no server world) |
| `stations.fill` | sigdb feature (`server.d/stations.json`) | core (sigdb); kiln-items.dll lowers it (`faulted`) when kiln.world refuses the program `kiln_items_stations` or a fill faults (off until restart) | C1, C2, C5 | `Stations.Fill`: the game's own fill from chests with no player |
| `stations.control` | sigdb feature (`server.d/stations.json`) | core (sigdb); kiln-items.dll lowers it (`faulted`) with `stations.fill` | C1, C2 | `Stations.Stop`, `Resume`, `Select` (the bytes the game's stop and select write) |
| `stations.events` | sigdb feature (`server.d/stations.json`) | core (sigdb); refused subscriptions name the reason where kiln.stations is not here (a subscription before a world runs is accepted) | C4 | `StationStateChangedEvent`, `StationPlacedEvent`, `StationRemovedEvent`, `BasesChangedEvent`, `StationCraftedEvent`, `StationPlayerActionEvent` |
| `stations.output_sources` | sigdb feature (`server.d/stations.json`: `@stations/AutoTransferRecipe.take`, the fill's per-item take) | core (sigdb); kiln-items.dll keeps it off without `stations.fill` or when the input-stack layout constants do not check out; Kiln.Blocks lowers it (`service-missing`) where kiln.stations' table has no output-source members (an older kiln-items.dll) | C7 | `Stations.SetOutputSource`, the fills that take, last, from opted-in stations' outputs (`Stations.Fill`, and the station screen's fill button on the server); since 0.2.0-dev.29.17 |
| `stations.craft_sources` | sigdb feature (`server.d/stations.json`: `rules/InventoryPay`, `rules/RecipePaid`, `gems/PayMaterial`, `stations/BaseStockForPlayer`) | core (sigdb); kiln-items.dll lowers it (`symbol-missing`) when the craft's payment call is not at `RecipePaid` or a symbol is missing, and at the first world when kiln.rules' inventory-payment hook is not in (`KILN_RULES_HOOK_PAYMENT`): `policy` while Creative Mode runs, `service-missing` without kiln-rules.dll, `faulted` otherwise; Kiln.Blocks lowers it (`service-missing`) where kiln.stations' table is older | C8, C9, C12 | the same opt-in as the last source of a player's craft, only when the outputs of the player's base cover every line's shortfall; a shortfall stays the game's; since 0.2.0-dev.29.17 |
| `stations.craft_ui` | sigdb feature (`client.d/stations-craft-ui.json`: `rules/client.CraftableAmount`, `stations/ResolveRecipeInputs`, `content/RecipeFind`) | core (sigdb); kiln-rules installs its crafting-count hook when this or `rules.craft_ui` resolves; kiln-items.dll lowers it at the first client world when that hook is not in (same reasons as above) or the recipe reads did not resolve; Kiln.Blocks lowers it where kiln.stations' table is older | C8, C9, C11 to C15 | a Kiln client's crafting menu counts the opted-in outputs the server sends (`kiln:craftstock`, not required) while a menu is open; a vanilla client keeps the game's counts |
| `gameplay.craft_phase` | sigdb feature (`server.d/gameplay.json`: `@gameplay/CraftRecipe.factory_phase`) | core (sigdb); kiln-gameplay.dll logs "craft phase on/off" at its start | - | an automated station's cycle is one `ItemCraftedEvent` (`KILN_GAMEPLAY_CRAFTED`), at its end; without it the old behaviour (its start and its end) |
| `content.text` | service row, session (since 0.2.0-dev.29.18) | kiln.content (kiln-content.dll) at its start, before the mods load: `wrong-side` on a dedicated server; `symbol-missing` on a client build without the game's text lookup (`content.loca`, or `content.new` off); `faulted` when the hook on it could not go in; pending until the game looks up its first text (its title screen does, right after boot), then available for the process. Kiln.Content says `service-missing` / `service-older` without kiln.content or with one from before the slot | the real-client checklist in [content.md](content.md#the-games-own-texts) | `Content.Text`, `Content.TextAvailable`, `Content.TextLanguage`: the game's own text of a loca tag in the player's language |

### Gameplay hook groups

The sigdb rows `gameplay.craft`, `gameplay.terraform`, `gameplay.spawn`, `gameplay.entities` and `gameplay.build`
continue to describe resolved symbols/layouts. Native-owned session rows `gameplay.craft_hooks`,
`gameplay.terraform_hooks`, `gameplay.spawn_hooks`, `gameplay.entities_hooks` and `gameplay.build_hooks` describe
installation: pending with "resolved, not installed: no mod asked", pending with a safe-point wait, available when
installed, or unavailable with the refusal reason. Session rows must not be manifest feature requirements.
The existing static API aliases `gameplay.entity_events` and `gameplay.spawn_replace` remain available while their
group resolves and lower on permanent refusal; they do not become pending and block admission of old manifests.
All five groups install on pre-world demand or checked safe Pre, sharing at most two target attempts per tick
and three attempts on thread-check refusal; after the third the row stays pending ("waiting for the next no-world
moment") and the group installs when no world runs (a client's menu; a server's next start). With no world running, a
thread-check refusal installs unchecked at once. Build's five targets are behind the other five: three Pres alone,
five Pres for simultaneous all-group demand without retries. Refused partial groups stay inert. No announcement from a demand-aware Host triggers legacy all-group installation: at the first
client menu UI tick with no world, or in one batch at the first eligible world-start Pre; retries are paced. A legacy status probe before any announcement also requests all groups;
modern announced-host status probes preserve idle groups.
Native `request_hooks(owner, mask)` is appended after `request_build`; build now uses the same checked path. Mask 0 announces the new Host before scanning. Managed
reference preflight requests all groups, conservatively including crafting-only and shipped-helper references.
See [gameplay](gameplay.md#craft-terraform-natural-spawn-and-entity-hook-demand) for consumers and pending-call behavior.

Pre-world reference demand includes the indirect consumers: `Kiln.Creatures` -> spawn hooks; `Kiln.Quests` -> craft,
build and terraform hooks (`Kiln.Rules` -> none: it asks for build and terraform when a rule set keeping builds outside
bases is applied). Direct `Kiln.Gameplay` references request
all groups. `Kiln.Blocks` and admitted `blocks/` data packs request build/craft/terraform/spawn. The host requests only additional group bits as helper assemblies load; its 136-byte table fallback
continues calling request_build when needed, while the older service owns the other startup hooks.

Admitted `quests/` packs request craft/build/terraform (mask 7). Unreadable or over-budget metadata requests all
groups; reflected/path-loaded use outside the inspected closure requests at the first API call. The Host caches
metadata across mods within a bounded pass and logs its assemblies-read count and elapsed milliseconds.

## Rows added by the gem wave
Managed rows of Kiln.Abilities' gem API ([gems.md](gems.md)), reported at its start (the first service tick), at the
first use of `Gems` or a gem event, and about once a second.

| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `gems.read` | managed, session | Kiln.Abilities | `Gems.Catalog`, `Get`, `Equipped`, `Socket` and `GemSocketChangedEvent`: needs kiln.items (`service-missing`) with item instances (`items.instances`, with its reason when off), kiln.types and the game's reflection of `Equipment.equipmentImpacts[].weaponGemImpactEntityId` (`symbol-missing` on a build without it), and kiln.world. Available with the note "without kiln.gamedata: Gems.Catalog has no programs or configs" when kiln.gamedata is missing. A member that needs another service's row throws `KilnUnavailableException` naming `gems.read`, the other row in its text. It does not wait for a server world: like `items.instances` it reads available without one (the main menu, a joined client), where `Socket` throws `KilnUnavailableException` (`gems.read`), `Equipped` lists no gem and no `GemSocketChangedEvent` is posted |
| `gems.triggers` | managed, session | Kiln.Abilities, with `gems.read` | `GemTriggeredEvent` and `Gems.Register`'s gems with C# handlers: needs `gems.read` (its state), kiln.abilities (`service-missing`) and its native events (`abilities.natives`, its reason when off). `Gems.Register` also checks kiln.content's new items and, for a trigger, kiln.assets itself (it throws `KilnUnavailableException` naming `gems.triggers`; the row does not cover them) |

The gem wave's two other rows are sigdb features, in the tables above: `gems.craft` (the forge actions, their events and
`Gems.ReturnSocketedGems`' forgeless return; [gems-forge.md](gems-forge.md)) and `gems.sockets` (server: sockets on other
gear; client: the widened forge lists; [gems-sockets.md](gems-sockets.md)); Kiln.Abilities lowers `gems.craft` where
kiln.gemcraft is not here and `gems.sockets` where neither kiln.items v7 nor kiln.ui.values' `gemforge_sockets` is. All four
rows in one place: [gems.md](gems.md), "Capability rows".

## Rows of the task pool and the engine profiles
The core's row for Kiln's task pool ([tasks.md](tasks.md)), and kiln.engine's rows for the exact engine profiles
([engine.md](engine.md)) that the task-thread world reads build on.

| Row | Kind | Reported by | What it says |
|---|---|---|---|
| `tasks.pool` | core, static (it follows the pool's health) | core, from kiln.tasks' pool; checked again at every registry call, so a change reaches its subscribers on the calling thread | Kiln's task pool runs work: C# `Context.Tasks` and native `kiln.tasks` ([the mod author's guide](../modding/tasks.md)). Available on a booted runtime on both sides, the dedicated server and the client. Unavailable: `policy` in safe idle ("not started in safe idle"), `service-missing` when kiln.tasks did not start, `faulted` while no task worker can run (a start failed or every worker was lost; retried every second). Never pending, so a manifest can require it (`<KilnFeatures>tasks.pool</KilnFeatures>`, which also answers KILN121 for `Context.Tasks.Run`). It says that tasks run, not that they can get back to a tick: with no live server world here (the main menu, a client joined to someone else's server) the hops to the tick refuse with `KilnUnavailableException` naming it |
| `engine.profile` | native, static | kiln.engine, at its start | one of Kiln's exact engine profiles matched the running game image; the text is the profile's id. Unavailable, `symbol-missing`, on every other build, the text saying why (no profile for this build, an image that could not be identified, more than one match) |
| `engine.ecs_read` | native, static | kiln.engine, with `engine.profile` | that profile's owner and ECS read layouts are validated (`symbol-missing` "the profile has no validated ECS read layout", or `engine.profile`'s reason) |
| `engine.ecs_write` | native, static | kiln.engine, with `engine.profile` | as `engine.ecs_read`, and the profile's original native-thread Scene write gate is enabled (`symbol-missing` "the profile has no enabled native-thread Scene write gate", or `engine.profile`'s reason) |

A mod asks `world.ecs_read` (`WorldConcurrent.IsAvailable`) for the task-thread world reads and `world.ecs_write`
(`WorldConcurrent.CanWrite`) for the compare-exchange writes, not the `engine.*` rows: those say what the profile
carries, the `world.*` rows say whether kiln.world reads or writes with it in this process (both are among the
kiln.world rows above).
