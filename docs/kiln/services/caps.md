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
Some capabilities ship switched off because only a real game client can confirm them (a camera, a marker on the
compass, what a joined vanilla player sees). Such a capability is **held** until its guided client checks
([client-pass.md](../client-pass.md)) have passed in a real game.

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
  `kiln.log`. The guided pass runs with `"preview": true`.
  ```json
  { "preview": ["camera.fov"], "client": { "preview": true } }
  ```
- **Opening a capability for good:** remove its entry from the list in the commit that records the passed checks.

## Rows the core fills
- Every sigdb feature, source `sigdb`. An unavailable one says `symbol-missing` with the symbol, `policy` in safe idle,
  or `faulted` for the `tick*` features and `world.lifecycle` when their hook is not installed or faulted.
- Every in-core service and every published service, `kiln.<service>`, with `v<n>` as text.
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

| `world.tracked_writes` | managed, session | Kiln.World, at the first use of a tracked write or of `World.SupportsTrackedWrites`, then every 25 steps | `World.Write`, `World.Update`, `World.WriteField`, `World.WriteAt` work: kiln.world v3 with the game's tracked write path (`world.programs`, `world.read_other`, `world.names`, and `items.fuel`'s symbols; `symbol-missing` naming the first that is off, `service-older` for a kiln.world without v3). Pending while the write program waits for a server world; `faulted` when the game did not take the program or one of the game's calls faulted. Available with the note "no loaded mod lists a component" when nothing is listed: every write then answers `NotDeclared` |
| `world.component_bits` | sigdb feature | core | the game's per-component save and replication bits can be read (`WorldWriteResult.SaveTracked`, `.ReplicationTracked`, `World.WriteInfo`); off: those answers are null, the writes work the same |

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
| `gameplay.entity_events` | managed, static | Kiln.Gameplay | the entity lifecycle events work (`Gameplay.EntityEventsAvailable`): needs kiln.gameplay and `gameplay.entities` |
| `gameplay.spawn_replace` | managed, static | Kiln.Gameplay | natural spawns can be replaced (`Gameplay.ReplaceNaturalSpawns`): needs kiln.gameplay with the replacement function, `gameplay.spawn`, and the random spawners' hook in (`faulted` when it is off) |
| `gameplay.*` (ten), `build.*` (five), `skills.functions`, `skills.login_guard`, `skills.shadow`, `skills.tree_load` | sigdb features | core; Kiln.Gameplay, Kiln.Build and Kiln.Skills lower theirs when their service is not loaded | what kiln.gameplay, kiln.build and kiln.skills serve |
| `content.references` | managed, static | Kiln.Content | this kiln.content sets references (`ContentEntry.SetRef`, `SetPerks`; table v4) |
| `content.ready` | managed, session | Kiln.Content, at its start and every 25 steps | the registries are loaded and the data packs applied (`Content.Ready`); unavailable without kiln.content |
| `content.*` (eight, server side), `gamedata.template_clones`, `gamedata.buff_lookup`, `creatures.*` (ten) | sigdb features | core; Kiln.Content and Kiln.Creatures lower theirs when their service is not loaded | what kiln.content, kiln.gamedata and kiln.creatures serve |
| `quests.*` (six), `ui.*` (eight, game client), `render.camera` | sigdb features | core; Kiln.Quests and Kiln.Ui lower theirs when kiln.knowledge, kiln.ui or kiln.render is not loaded | what kiln.knowledge, kiln.ui and kiln.render serve |
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
Rows a dev.28 package adds, by service. "Held" names the guided checks of the client gate (above) that open the row;
a held row reads `unavailable`, `policy`, `game test pending: <ids>` until then.

| Row | Kind | Reported by | Held until | What it says |
|---|---|---|---|---|
