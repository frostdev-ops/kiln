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

What a manifest's `features` can require of these: the sigdb features (`items.fuel`, `items.move`, `items.names`,
`items.transfer`, `items.instances`) and `anim.player_states`. The manifests are checked before the managed services
start, so for a sigdb feature the check sees the sigdb verdict only, not a later lowering by Kiln.Items: ask
`Capabilities.Is` at run time as well. The managed rows exist only once their service has started (`blocks.*` only
after the first item block compiled), and `items.moves` and `saves.instance_data` depend on the session: a manifest
naming one of them is refused, with the advice to check `Capabilities.Is` at run time.

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
