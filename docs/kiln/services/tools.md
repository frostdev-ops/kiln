# Building tools

Since **0.2.0-dev.30.5**. `Kiln.Tools` supplies declarative terrain actions and owned material observations;
`Kiln.Blocks` supplies the tool, brush and material-pool declarations. Every participating game needs the same mod
(`clients: mod`). No game address, borrowed view or engine function belongs in a mod.

## Declarations

```csharp
var materials = Blocks.Define("loose-stone", new ToolMaterialPoolBlock {
    Name = "Loose stone",
    Entries = [new("Build_TerrainMaterial_T1_Rubble", 8, 16)],
});
var small = Blocks.Define("small", new TerrainBrushBlock {
    Name = "Small stone brush",
    Description = "A small sphere of loose stone.",
    Shape = TerrainBrushShape.Sphere,
    Radius = 0.5,
});
var tool = Blocks.Define("stone-tool", new BuildingToolBlock {
    Name = "Stone tool",
    Description = "Places and clears loose stone.",
    MaterialPool = materials,
    Brushes = [small],
    Primary = new TerrainToolAction { Mode = TerrainToolMode.Add },
    Secondary = new TerrainToolAction {
        Mode = TerrainToolMode.Remove,
        RemoveMaterials = [8],
        TerrainOnly = true,
    },
});
```

A pool contains **1–64 distinct items and distinct terrain cells** (1–127). `VoxelsPerItem` is 1–1,000,000;
placement pays the ceiling of actual added volume divided by that rate. It is not inferred from the material's mining
drop. Native compilation checks that each item actually places its declared terrain cell **and belongs to the
qualified vanilla `TerrainMaterials` pool**. A non-member is a compiler error, not a fallback to a broader pool.
A tool without an Add action may omit its material pool.

A brush is a sphere of radius **0.25–8 metres**. Its own item identity, name and description are separate from the
root tool. The tool owns its brush membership; declaring a brush part alone does not publish an orphan item into
vanilla Overgrowth's family. A family has at most 64 distinct brushes; the game's picker shows the first **16**, in
the order the tool lists them (its brush list is a fixed 16-row array). Each brush item is based on vanilla
`Building_Overgrowth_Shape_Small`; its radius is that item's own impact config `0x5258131e` (BrushRadius, the value the
tool's programs read), written as the item rule `impactValues.0x5258131e`. The cursor preview is no radius: it is the
shape item's own `equipment.cursorVfx`, one fixed effect per vanilla size (Small 0.5 m, Medium 1.5 m, Big 2.5 m). A brush
takes the effect of the vanilla shape nearest its radius (below 1 m Small, below 2 m Medium, from 2 m Big), so the preview
shows one of those three sizes while the stroke uses the brush's exact radius. The effect is only in the brush's
definition: a mod's own content layer on its brush item's `equipment.cursorVfx` applies over it.
`Identity = BlockIdentity.Legacy(guid)` or
`Legacy(itemId)` preserves an existing saved item; `Was` preserves the derived identity after a Kiln key rename.
An input bound to a Remove does not ask the game's build cursor for a selected building material
(`equipment.cursorConfig.checkRequiredMaterialsOnPrimaryAction` / `...OnSecondaryAction` false on each brush): the base
brush asks on primary, for its Add, and the game refuses the stroke while that check fails. An Add keeps the base's
setting.
`Icon` on the root accepts a PNG in the mod's folder.

The pool is an immutable **Kiln policy subset**, not a new game-visible pool row. Root and brush items reference the
existing vanilla terrain pool; the held tool's picker filters its rows to the policy subset. Registering a policy leaves
vanilla pool bytes, rows and magic-stock enumeration unchanged. Adding a second row of the same items would count
magic-chest stock twice, even with a vanilla tool held, so that lowering is forbidden.

The compiler also emits raw-fill brush entries in the game's blueprint registry, item definitions, and per-action
Impact programs and action sequences. It does not add terrain entries to `BuildingMaterialPoolBlock`. New table
metadata and brush definitions can be pending until the next game start. A pending definition is not proof that the
game has ingested or drawn it. A tool pool kept for the next start is read back at that start as a tool pool (until
0.2.0-dev.31.20 it was read as a building pool and refused, so a tool with an Add action never registered: kiln#152).

## Execution and safety

The game's building input and held-repeat cadence remain in charge. Policies are immutable native snapshots; no
managed delegate runs on a simulation worker. The authoritative game pipeline performs permissions, payment, mining
yields and history. Required hook mappings must all be qualified before a family action is admitted. Missing mappings
refuse; they never fall through to unrestricted digging or an independent inventory debit.

Root identity is derived from the root item's GUID and the same name/description text-tag purposes as ordinary Kiln
items. It does not borrow vanilla Overgrowth's identity. HUD material rows are compacted only in owned, bounded
storage, and their view is rebound to that storage after the game constructor returns. A stale selection, an empty
stock entry, a different tool, a different player or a different session is not a valid selection.

## Material observations

`ToolMaterials.TrySnapshot(actor, tool, out snapshot)` is read-only. A successful `ToolMaterialSnapshot` owns at most
64 immutable `ToolMaterialStock` entries, with backpack and connected magic-chest counts. It retains no game pointer.
A snapshot is **not authority** for payment or editing: a native invocation rechecks its current policy and game
supplies. An unavailable producer returns `false`; Kiln does not invent counts or return another player's cached view.

## Capability and qualification status

| Capability | Meaning | Refusal |
|---|---|---|
| `tools.actions` | The complete authoritative action pipeline is qualified. Its text (and that of `tools.actions.remove` / `tools.actions.add`) ends with how many tool programs this process bound, the last stroke's state and every stroke counter ("Reading the action trace"). | Missing required hook/context, offline or no session. |
| `tools.material_lookup` | The installed invocation-scoped lookup refuses terrain cells outside an Add policy's material pool. | Missing lookup hook, offline or hooks disabled; supported on client and server. |
| `tools.material_picker` | Terrain group2 is filtered only when the owned held-tool observation and the current call's resolved item agree; the game's aggregate supply counts are retained. | Missing selector/held-tool mapping; client-only presentation on a server. |
| `tools.material_snapshot` | A separate per-player backpack/magic-chest observation producer is qualified. | Currently unavailable: aggregate selector counts are never fabricated into two sources. |
| `tools.preview` | The explicit-item preview has an owned bounded row adapter. | Missing constructor mapping, wrong side or hooks disabled. |
| `tools.brush_families` | A checked call-site adapter changes only the current tool frame's owned brush-query GUIDs and writes the held family's rows into that call's fresh output. Its text ends with the picker trace ("Reading the brush picker trace"). | Missing exact frame/query/held-tool context, checked-hook refusal or wrong side. |
| `tools.identity` | The explicit-item root identity adapter is qualified. | Missing root helper, wrong side or hooks disabled. |

The native client-Post observer copies the actual main-hand/action selection into an owned scalar snapshot tagged by
actor, owner, generation, session and step. A missing, changed or older-than-one-step sample does not admit a tool.
Admission also checks the current selector call's resolved held item with guarded reads: a switch to a vanilla item
within that step, a missing pointer, a failed read or a root mismatch leaves every group untouched. It filters only
terrain group2, leaving all six other categories unchanged. The qualified preview uses each call's explicit item,
independently of that held observation. Presentation calls mask any outer impact policy for their full duration and
restore it on return, including nested vanilla calls. Failed owned-row reads leave an empty presentation, not an
unchecked pointer or an inherited selection.

The brush adapter is confined to the qualified frame drawer's call instruction. It requires the selected hotbar item to
be the held family's root or one of its brushes, and the resolved item to be that hotbar item. Picking a row puts that
brush in the actionbar slot (the game's `CreateBuildingItemAction`, as a vanilla Overgrowth shape takes its tool's
place), so after the first pick the slot holds a brush, also after a save and reload; the frame stays the family's. The
host takes the pick from the client's `createBuildingItemAction` and checks only that the picked item exists and, like
the item it replaces, carries one of four item flags (`IsTerraformer` for a brush): no tag, game list or family. A
dedicated server therefore puts a family brush in the slot as a single-player host does (read statically on server
26430253 and client 26430625, [docs/re/building.md](../../re/building.md) 1.2.2). The game's brush query scans only the
item list `ItemRegistryResource.terraformingTools` in the game's data, never the runtime item registry, so a family's
brushes (runtime items) are never in it, whatever their tags. On admission the adapter writes the family's own rows
(item, ratio i+1 over 1) into that call's fresh, empty output and replaces its include/ratio GUID values with the family
identity, which no listed item carries: the native query appends nothing and sorts the rows by ratio, which keeps the
declared order. Brushes carry no family tag. Other query sites, vanilla tools, callbacks and nested unrelated frames
inherit no admission. The query's three other call sites (`0xd180a0`, `0xd20190`, `0xd6aa05`) sit in functions that no
code reaches on client 26430625, so the frame drawer's list is the only list of brushes the player sees or picks from.
If this capability is unavailable, the ordinary picker has no established route to the tool's own brushes; do not assume
that Add/Remove is usable through the normal menu.

### Reading the brush picker trace

Every way out of the adapter is counted on the client (relaxed counters; the frame hook never allocates, locks or
logs). A client Post publisher turns a change of state into **one** `kiln.log` line and refreshes the
`tools.brush_families` capability text (`Capabilities.Why("tools.brush_families")`, `kiln/state/capabilities.json`), at
most once a second. When states change faster than that, the line names the state now and how many changes it covers.

```text
tools: brush picker: patched (the family's brushes were written into the picker); frames: no held family 212, patched 40 (3 rows)
```

| State | What it means | Look at |
|---|---|---|
| `disabled` | The adapter retired after a write fault (an adapter that never installed counts nothing: the `tools.brush_families` reason says why). | `tools.brush_families` reason. |
| `no held family` | The picker drew without a registered family in hand: a vanilla tool, or no current held-item sample. Normal while a vanilla tool is held. | Shown while the mod's tool is held: the family was never defined (a `blocks:` error at content ready) or the held-item observer has no sample. |
| `no identity` | The held family lost its identity record between two lookups (a removal racing the frame). | Mod reloads. |
| `other frame` | Not the Overgrowth branch's registers (the Decay branch, or another layout). | The root tool's base. |
| `not selected` / `not resolved` | The picker's selected hotbar item is neither the held family's root nor one of its brushes, or its resolved item is another. | Hotbar selection versus the held item. |
| `output not fresh` / `tag mismatch` | The call's brush list or include/ratio tags are not the vanilla Overgrowth frame's. | Another mod patching the picker; a game update. |
| `read fault` / `write fault` | Reading or writing the frame faulted; a write fault skips that query and retires the adapter. | A crash report next to it. |
| `patched` | The family's rows were written; `(n rows)` is how many. `truncated to 16 rows` counts families with more brushes. | If the picker still shows no brush after this, the rows reached the query and the miss is past it. |

No line at all while the tool's picker is open means the qualified frame drawer never reached this call.

### Reading the action trace

Since 0.2.0-dev.30.8 (kiln#133) every attempted stroke of a tool's Remove or Add action is traced where the game decides
it: in the process that runs the **server world**. That is your own game when you play alone or host, and the dedicated
server when you join one; a joined client's own trace says nothing about the stroke. In the impact hook the trace
only counts (relaxed atomics and a thread-local tally of what the terrain kernel saw; the trace itself takes no
allocation, lock or log). The tools service's server tick turns a change of state into **one** `kiln.log` line, at most
once a second, and keeps the last state and the counters on the `tools.actions`, `tools.actions.remove` and
`tools.actions.add` capability text (`last stroke: removed; strokes: removed 4, ...`; `Capabilities.Why(...)`,
`kiln/state/capabilities.json`). When states change faster than that, the line names the state now and how many
changes it covers. A process without a bound tool family traces nothing: vanilla terraforming passes through unread.

Each process that publishes a family logs it once, when the family's blocks are ready:

```text
tools: Stone tool: actions bound: primary Add (program 0x5a1c03e2), secondary Remove (program 0x1d0e8c47), 1 brushes; strokes are traced on tools.actions
```

and every capability text above says `tool programs bound here: 2` (two programs per family with both actions). On the
server-world process, `tool programs bound here: 0` means the family was never published there: look for a `blocks:`
error at content ready, or a mod missing on that side. A stroke then looks like this:

```text
tools: action attempts: removed (allowed terrain was removed) (program 0x1d0e8c47, UsedItem 0x8b3f0d11, radius 0.50 m, game result 0 done); strokes: removed 4; other programs 2
```

The parenthesis names the last stroke's program, its `UsedItem` and brush radius (when it got that far) and the game's
own result: code 0 with `done` (the game's building operation finished) or `not done` (it stopped, for example because
its building cursor refused the stroke), 6 cancelled, 0x11 internal, 0x18 missing argument. "other programs" counts
terrain impacts of programs no family binds here (pickaxes, explosions, vanilla Overgrowth): they never change the
state.

| State | What it means | Look at |
|---|---|---|
| `unreadable` | A terrain impact's context could not be read while a family is bound; it was refused. | A crash report next to it. |
| `closed` / `family removed` | The service is shutting down, or the family was removed as the stroke started. | Mod reloads. |
| `faulted` / `continuation fault` | A stroke faulted inside the game's terraform; tool actions stay off until the game restarts. | The crash report; `tools.actions` says `faulted`. |
| `not ready` | A hook the action needs is not installed. | `tools.actions.remove` / `.add` reason. |
| `no access` | The impact has no program access, program context or build zones. | A game update. |
| `not the server world` | The impact ran in another world than the authoritative server world, or none runs yet. | Possible between a world's start and its first tick; the game runs impacts only in the server world, so anything else is worth a report. |
| `layout` / `mode marker` / `aliased` | The program's arguments are not the tool program's (another mod rewrote it). | Other mods editing impact programs. |
| `no actor` | The impact has no acting player. | Only player strokes are admitted. |
| `no UsedItem` | The action that spawned the impact carried no item. | The brush's sequences (another mod replacing them). |
| `radius out of range` / `position` | The brush radius is not 0.25–8 m, or the stroke's position is out of range. | The brush's `impactValues.0x5258131e`. |
| `not a family item` | `UsedItem` (shown) is neither the tool nor one of its brushes. | Another family's brush bound to this program. |
| `radius mismatch` | `UsedItem` belongs to the family, but no brush has the radius the impact carries (shown). | Another mod editing the brush's radius. |
| `outside base` | The whole brush is not inside one base area. | Stand inside a Flame Altar area, away from its edge. |
| `no memory` | Kiln could not allocate or lock what the stroke needs. | Memory pressure. |
| `game stopped` | Admitted and run, but the game's terraform stopped before carving (a Remove) or did not finish (an Add, `game result 0 not done` or another code): its own cursor (no permission, too far, enemies nearby, missing materials), range or zone checks. | The building cursor's hint while aiming; the game result. |
| `no undo record` | Inside a base the game needs a per-player undo record for the stroke and none could be created, so it cancelled the stroke. | The game's undo history; mods that change it. |
| `not removable` | The brush reached the terrain, but no cell the action allows can be mined by it (all its coefficients are zero). | The action's `RemoveMaterials` against the terrain cell ids (unbreakable or unknown cells). |
| `nothing matched` | The brush reached the terrain, but none of its allowed cells were under it. | What was under the brush; aim at an allowed material. |
| `carved` | The brush reached the terrain; how much it removed could not be read (the kernel's report layout did not resolve from sigdb, or the game named no report). | Whether the terrain changed. |
| `removed` | Allowed terrain was removed. | Yields and undo are the game's. |
| `added` | The game finished an Add stroke (`game result 0 done`). | The game's payment and history. |

No line at all on the server-world process while strokes are attempted (and `tool programs bound here` is above 0)
means the tool's program never reached the game's terrain impact there: the action did not start (the building
cursor refused the input), or its sequence spawned another program.

Managed code reads this trace only through the capability text in this release; typed access to the counters
(`KilnToolsStatus` already carries the totals `dispatched` and `refused`) comes with a later mod API version.

**Still unavailable:** the per-actor producer that separates backpack and magic-chest observations. Static qualification,
filtering fixtures and native runner tests do not establish actual menu rendering, network behavior or the complete
real-game tool. **Seen in a real game** (0.2.0-dev.30.7): the picker lists a family's brushes. **Not yet seen in the
real game:** a stroke that changes terrain. Until 0.2.0-dev.30.8 every stroke was refused before the game's terraform
ran (the binding compared the wrong pointer with the server world), and a Remove on primary also failed the game's
material check; 0.2.0-dev.30.8 is the first release whose strokes can reach the terrain. Unseen with it: the action
trace's own lines for a real stroke (its states, the removal report it reads and the result's `done` byte are checked
statically and on host fixtures only), the sound and effect a Remove on primary plays (the base brush's primary is its
Add), the brush radius (`impactValues.0x5258131e`), yields, undo, Add payment, and the brushes' terrain model
(`equipment.terrainVoxelObject`; vanilla Overgrowth shapes carry none, only the game's terrain blueprints do: with one,
the server cursor's material check takes its voxel-object branch instead of the terraformer one (docs/re/rules.md 2.1),
which matters only to an input that checks materials, an Add). The item-presentation constructor's traced caller uses
the selected hotbar model, which need not be the running action's item; its last item must never be cached as
evidence of what the player holds.

## Checks without running the game

`native/services/tools/tests/test-ammo.sh` runs the presentation fixtures on the host, among them the brush-frame
adapter (`brush_frame.cpp`: rows, declared order, every refusal) and its trace (`brush_trace.cpp`). `test-impact.sh`
runs the action trace (`action_trace.cpp`: names, stroke outcomes, publication once per change) and the impact binding
over the engine's iterator layout, every exit recorded (`impact_dispatch_test.cpp`); `test-terrain-carve.sh` checks
what the filtered kernel notes for a stroke (minable allowed cells, the removal report, a stroke left unmeasured without
its layout). `terrain_report_sigdb_test.py --server <exe> --client <exe>` resolves the report layout on both game
executables (read only). The managed `ToolCompilerContentTests` (Kiln.Blocks.Tests) check what `ToolCompiler` emits for
a removal-only tool with three brushes; kiln.content's host tests check item tags and voxel models (`itemtag_test.cpp`)
and served terrain models (`refs_test.cpp`).

`test-tool-content.sh` runs that emitted content through kiln.content's own resolvers with the client's real
reflection and game data: definition fields and references, item rules, itemdata rules. Its negative controls are the
three refusals of the first real-game run (a radius rule on a bare config id, a root GUID as an item tag, an unserved
terrain model). It needs files this repository does not hold, so it is not part of the regular suites, and without
them it prints `SKIP` and exits 0:

```sh
# 1. the compiler's export (any machine with the .NET SDK)
mkdir -p out && KILN_TOOL_CONTENT_FIXTURE=$PWD/out/compiler.json dotnet test managed/tests/Kiln.Blocks.Tests --filter ToolCompilerContentTests
# 2. an installed game, only read as files (KILN_GAME defaults to ~/.local/share/Steam/steamapps/common/Enshrouded;
#    KILN_CLIENT_EXE names another copy of its client exe)
KILN_GAME=<game folder> native/services/tools/tests/test-tool-content.sh out/tool-content out/compiler.json
```

The script extracts the game inputs into `out/tool-content/inputs` (`extract_tool_content_inputs.py`, read-only).
Inputs extracted earlier can be passed instead: `test-tool-content.sh <out> <compiler.json> <types.json> <brush.bin>
<pool-item.bin> <registry.bin> <shape-medium.bin> <shape-big.bin>` (the two vanilla shapes whose cursor preview larger
brushes take).

Only the player runs the real-game check: confirm root/brush names, independent vanilla Overgrowth choices, all pool
entries with backpack-only and magic-chest-only supplies, empty stock, switching between tools, inventory changes,
selection after leaving/rejoining a world, paid placement/removal and the game's normal/remapped undo.

## Changelog

- **0.2.0-dev.31.21:** picking another brush in the picker keeps the tool's own brushes. The picked brush takes the
  actionbar slot, and the adapter admitted only the root there, so every later frame listed the vanilla Overgrowth shapes
  and the next pick handed the player the vanilla Overgrowth Tool (`not selected`, then `no held family` in the trace).
  Each brush previews at the vanilla size nearest its radius (every brush kept the small shape's preview). A tool pool
  kept for the next start is read back as a tool pool, so a tool with an Add action registers at the second start
  (it was refused at every start). Thanks SonjaNemitz (kiln#152, kiln#153).

- **0.2.0-dev.30.8:** a tool stroke reaches the game's terraform. The impact binding took the impact iterator's first
  qword, the engine's program context, for the world, so every stroke was refused as "not the server world"; it now
  reads the world from that context's job. A Remove action no longer asks the game's build cursor for a selected
  building material on its input (the base brush asks on primary, for its Add), which refused every Remove bound to
  primary. Every tool stroke is traced: one `kiln.log` line per change of state and the `tools.actions` capability
  text, which also says how many tool programs the process bound ("Reading the action trace"); each published family
  logs its programs once. An Add counts as added only when the game finished it (a cursor refusal also returns code 0),
  and the removal report the trace reads is captured from the game's kernel by sigdb on both builds. Thanks bisdat
  (kiln#133).

- **0.2.0-dev.30.7:** the picker lists a family's own brushes (the game's query scans only its data's
  `terraformingTools` list, so they were never found); brush radii reach the game (`impactValues.0x5258131e`); no root
  GUID is written as an item tag; brush terrain models resolve; the brush-frame trace in `kiln.log` and
  `tools.brush_families`; a content check against the installed game's data (the game is not run).
  `equipment.terrainVoxelObject` takes only a terrain model and `voxelObject` only a shape's model in data packs and
  itemdata rules. Thanks bisdat (kiln#130).
