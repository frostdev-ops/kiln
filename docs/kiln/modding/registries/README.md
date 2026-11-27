# Registries

Kiln's registries are the game's data as **Kiln-owned names**: `items.maxStack`, `recipes.inputs`, `loot.stack.max`,
`drops.entries[3].chance`, `balance.levelCap`. A mod edits a field by its Kiln name and an entry by its Kiln id
(`game:material_t1_wood_logs`); it never names a game function, member, offset or reflection path. Kiln maps each field
onto the game for every game build it supports, so a game update breaks Kiln's mapping (which a Kiln update fixes), not
your mod.

Registries hold **data only**. Services and events (Kiln.Content, Kiln.Items, Kiln.World, ...) stay as they are: a
registry replaces raw data paths, not services.

**Status in this release: experimental (`KILNX031`), schema version 0.1.0.** The schemas, the generated C# types (`Kiln.Registry`), the field
catalog (`Kiln.Registry.RegistrySchema`) and these pages ship, and the registry stage reads a data pack's `registry` section at
start (fields that apply now or at world load also follow a pack's hot reload). Since mod API **0.2.17**, C# mods can author
through `Mod.OnRegistry(RegistryContext)` and use explicit owner-scoped live changes through `Kiln.Registry.Registry.Change`.
GameStart entry operations add items/recipes through Kiln's content definitions and hide items/recipes/loot without deleting game records.
Unsupported or unproven operations are refused with their reason in kiln.log and registry.json.
Until a registry is marked stable its shape may change in any
release; from API 1.0.0 the stable registries' contract is frozen (additive only).

**Versions.** Experimental schemas are 0.x; a schema reaches 1.0.0 when it is frozen with API 1.0.0. Within one breaking
line (0.<minor> while experimental, <major> from 1.0.0) a field may only be added, widen its range, get new enum values
at the end, or apply earlier; anything else (a narrower range, other values, another type, a later applies, a removed
field) needs the next breaking version (0.2.0 after 0.1.x; 2.0.0 after 1.x) and ships an id migration
(`schema/registry/migrations/<registry>-<from>-<to>.json`, Kiln ids and field paths old -> new). `schema/registry/ids.lock.json`
keeps every field's contract and every registry's version history; `tools/registrygen/registrygen.py` refuses the rest.

**What a migration does.** registrygen builds every migration into Kiln (one row per rename), and Kiln reads every edit
through them before it checks and matches it: a data pack's `registry` section, a C# or native mod's batch, a live change,
the plan a rollback starts on. A field path the running schema does not have (an edit's `field`, the fields of an add's
`fields` and of a `where`, the leaves of a list's elements), and a Kiln id (an exact selector, a `like`, a reference value
such as a recipe input's item, plain or as `{"set": ...}`), that a step renames is read as the new name, through every later
step too. Old names are never reused, so this is right whatever schema version the edit was written for, and a name the
running schema has is never renamed. A glob selector is not renamed. An edit that names one field twice, by its old name and
its new one, is refused (which value it meant is unknown). kiln.log (`registry: migrated ...`) and registry.json
`migrated` name every rename of a start's edits (a live change's: kiln.log, `registry (live): migrated ...`); a name no migration knows is refused as before ("items has no field ..."). C# mods name fields
through the generated properties of the Kiln.Registry they run with, so their edits always carry the running names. No
registry has a breaking step yet: every schema is 0.1.0, and Kiln ships no migration rows.

| Registry | Entries | Page |
|---|---|---|
| `items` | every item: stacks, rarity, levels, power, speed, damage, armour, costs, perks, socket | [items.md](items.md) |
| `recipes` | every crafting recipe: inputs, outputs, duration | [recipes.md](recipes.md) |
| `loot` | random loot per item: stack sizes, rarities | [loot.md](loot.md) |
| `drops` | the game's drop tables (default inventories) | [drops.md](drops.md) |
| `balance` | the balance table: experience, levels, flame, durability, sockets, perk costs | [balance.md](balance.md) |

The per-registry pages are generated from `schema/registry/<registry>.json` by `tools/registrygen/registrygen.py`; edit
the schema, never the page. Kiln maintainers adding a registry: [adding-a-registry.md](../../adding-a-registry.md).

## C# authoring (API 0.2.17)

Reference `Registry` in `KilnServices` and opt into experimental diagnostic `KILNX031`. Published entry types remain in
`Kiln.Registry`; the context and `Mod.OnRegistry` live in `Kiln.Api`. See the complete
[RegistryEdits sample](../../../../mods/samples/RegistryEdits/RegistryEditsMod.cs).

```csharp
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Stacks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registry.For(context);
        registry.Alter(view => view.Items["game:material_t1_wood_logs"].MaxStack = 500);
    }
}
```

The class attribute is constant metadata; Kiln reads it without running the mod. No attribute on a real override means
**GameStart**. Declaring points without an override (`KILN122`), zero/unknown flags, or automatic Manual (`KILN123`) is invalid.

| Point | Invocation and inputs |
|---|---|
| `GameStart` | A retained author instance, once per Add/Alter/Final pass at resources-ready. Global settings only. It is not the transient `OnBoot` instance. |
| `ModLoad` | Runtime instance after `OnLoad`, once the base registry exists. Global settings only, even when a world already exists. |
| `WorldLoad` | Runtime instance for each native world token, including a new world with no save id yet. Per-world settings remain unavailable until bound. Adoption reauthors this point with the bound id, replacing its earlier id-less layers. |
| `HotReload` | Replacement runtime instance, after the old owner's live changes are removed. When declared, it replaces `ModLoad` on reload; the two points never both run for the same replacement. A pass that replaces `ModLoad` keeps ModLoad's process lifetime (its layers follow the process into later worlds); if any of its phases read per-world settings, all of the pass's layers belong to that world and the pass runs again once the next world's settings are bound, replacing them. A `HotReload` pass of a mod without `ModLoad` is world-bound when a world exists. |
| `Manual` | Explicit `Registry.Change`, not an automatic class flag. Requires an active world. |

GameStart authors get the registry context, **not cross-mod runtime exports**. Declared dependencies still order the edits:
a dependency's edits run first. A mod that needs another mod's runtime export chooses ModLoad or later. A declared export
lookup at GameStart is refused with `RegistryContextError.RuntimeExportUnavailable` (assembly loading can wrap that error).
Private helper assemblies in the mod's own folder remain loadable; they are not another mod's runtime export graph.

Automatic points run all selected mods' Add, then Alter, then Final in load order. Ordinary property assignments are absolute and target
Alter; `registry.Final(...)` and `registry.Items.Final(pattern, ...)` explicitly target Final. At GameStart, Final handlers read the
Add+Alter view, never another mod's Final commit. A callback's own pending writes are projected into its typed read view;
they are submitted only after successful return.

**What runtime reads see.** A runtime point (`ModLoad`, `WorldLoad`, `HotReload`) works like GameStart's stage: every
callback of one pass, in every phase, reads the same pre-pass values, whatever the load order, and a mod's reads leave out
its own registry layers (its GameStart edits and its earlier live changes). So `item.MaxStack = item.MaxStack * 2` restates
the mod's intent at each point (10 becomes 20 at ModLoad and stays 20 at WorldLoad) instead of doubling again, and two mods
doing it in one pass both read 10. The pass's edits are committed only after every callback ran: all Adds, then all Alters,
then all Finals, each in load order (dependencies first), so an earlier mod's Final still beats a later mod's Alter. Between
passes the later commit wins (a ModLoad Final of 70 loses to a later Manual Alter of 80; reverting that restores 70).
`Registry.Change` and `Registry.Read` read the live values as they are, the mod's own layers included.

**Refusals per entry.** At ModLoad, WorldLoad and HotReload an edit that one selected entry cannot take refuses that entry
alone, named in kiln.log and registry.json, and applies to the others: an exclusive claim on that entry, an entry missing
in this world, a config the entry lacks, or a value its storage cannot hold exactly (an `items.manaCost x1.5` over
`Select("*")` where one item stores whole numbers: that item's reason starts with `not_exact: `). The result is `Applied`
with `Refused > 0` and the first reason. An entry refused at binding is left out for that world only; a ModLoad change
tries it again in the next world. GameStart edits bind per entry the same way at world load (a conflict at the stage itself
still refuses the edit). `Registry.Change` stays all-or-nothing: a binding or evaluation failure of any entry refuses the
whole transaction with the same reason, and nothing of it is written. A game memory write that fails after every retry is
different in kind: it takes back the whole change that owns the slot for that world, at an entry point too (never another
change), so a change is either all written or not at all; a take-back Kiln cannot prove is `Unverified`. A throwing callback submits none of that turn's buffered edits and
reverts that author's earlier commits from the same point. Other points' retained layers remain; a later point or world
can retry the author. When Kiln itself cannot take the pass yet (its begin or commit is refused because the live back-end is
still being installed, a budget is full, or the base is still being prepared), the point is not a fault: Kiln undoes that
pass's commits and authors the same point again on a later tick (the next tick first, then backing off to 5 s), logged once;
after ten tries it is logged as a fault with the real error. An exception from the author's own code is always a fault, a
`KilnUnavailableException` from a read inside `OnRegistry` (an unmapped field, an entry without that config) included.
`Select(pattern)` returns entries by Kiln id; list properties use the generated clear/append/remove/index APIs.
All three passes are invoked; entry operations are accepted only at GameStart. Runtime points wait for the host tick;
native resources-ready callbacks never execute runtime authors inline. Layers from distinct declared points compose:
for example, a WorldLoad append stacks on a retained GameStart append. Prefer absolute assignments or list replacement
when the same intended value is authored at several points.

Contexts carry `EntryPoint`, `Phase`, `World` (Kiln token, kind and bound save id), and `Identity`. Code, point/pass,
world identity and settings actually observed in that callback contribute to Identity. The stage keeps each start's plan
("Plans and rollback" below) but never starts from a kept plan by itself: only a rollback someone asks for does. There is
no plan cache keyed by what the edits read.
Global `Config.Register<T>()` and `WorldConfig<T>()` can be used as inputs; fallback authoring snapshots do not install
watchers or write defaults. Register live settings in `OnLoad` to get ordinary change events. Per-world settings are
refused at GameStart/ModLoad, and while a world's settings are unbound. Later points never write restart-only fields:
they return/report `RestartRequired` rather than silently applying them.

Keep copied values, not contexts/entries/lists. Their operations fail with `RegistryContextException.Code` after return
(`Closed`), unload (`Unloaded`), on another thread (`WrongThread`), or a world switch (`WorldChanged`). Metadata and copied
results remain snapshots. A resolved `Registry.Read(view => ...)` frame is expiring and read-only.

### Entry add and remove (GameStart only)

| Registry | Add | Remove |
|---|---|---|
| `items` | `AddLike(id, like, configure)` on the stage's Items collection | Hide from random-loot candidates and recipes; disable direct-drop slots that name the item. |
| `recipes` | `AddLike(id, like, configure)` on the stage's Recipes collection | Hide from station lists and the all-recipes/crafting view. |
| `loot` | Not supported | `Remove(pattern)` on the stage's Loot collection excludes random-loot candidates; it does not remove the item, its recipes or direct drops. |
| `drops`, `balance` | Not supported | Not supported; ordinary mapped field/list edits remain separate. |

```csharp
if (context.EntryPoint == RegistryEntryPoint.GameStart)
{
    registry.Add(view => view.Items.AddLike(new("my-mod:logs"), new("game:material_t1_wood_logs"),
        item => item.MaxStack = 100));
    registry.Alter(view => view.Loot.Remove("game:material_t1_wood_logs"));
}
```

AddLike requires an explicit present Kiln `like` id; clone chains end at a game entry. A recipe inherits its base's station,
inputs, outputs, duration and unlock rule. Configure uses the ordinary typed fields/lists, buffered as one add; a throwing
configure publishes nothing. New ids belong to the bound mod, are lower case, and cannot use a game's clash suffix.
Identity reuses Kiln's deterministic registry GUID rule; zero/colliding native ids are refused, never silently reminted.
Later stages can read/select the added id and later field-only changes can target it. An unmapped explicit override or
inexact whole-number config refuses the definition before its private clone is published. A native registration failure
refuses the entry for that world and propagates its root reason to dependent definitions; process intent retries next world.

**Removal hides; it never deletes.** Item and recipe records and id lookups stay intact. Already-owned removed items remain
usable in inventory, equipped slots and containers, including after save/load. Removing a recipe does not cancel an existing
crafting job. Existing game recipes needing or producing a removed item are hidden, not converted into input-free recipes.
A new entry depending on a removed/refused entry is refused whole, to a fixed point. A field edit introducing a bad reference
is refused for that target without dropping unrelated valid edits.

Direct-drop arrays keep their physical slots and indices: removal layers set affected `chance` and `weight` to zero rather
than resizing the list. Every required drop row and recipe dependency row must be proven on the running build; otherwise
item removal is refused **whole**, with no partial loot/recipe hiding. In particular, `drop tables not proven on this build`
means no item removal was applied. The current build records (server 1287369, since 31.x) prove the drop rows, so item removal
applies there; a later game build Kiln has not proven yet refuses it whole again. Missing/invalid group
metadata, or removing the last safely spawnable stack in a selector group, also refuses the whole item removal. A later
registry chance/weight edit cannot reactivate a disabled slot: removal wins and the conflicting edit is reported.

These are process decisions, not world settings. ModLoad, WorldLoad, HotReload and Manual entry requests report
`RestartRequired`; automatic later passes retain unrelated valid field edits. A **Manual draft containing any entry-operation
refusal writes none of its valid field edits**. Field-only drafts keep the existing filtering behavior: parse, mapping,
composition-conflict and restart-field refusals can coexist with accepted live field edits. A binding/evaluation refusal or
the candidate's own failed write, however, rolls back the whole Manual candidate; a failed rollback is `Unverified`.
A pack reload preserves the original GameStart adds/removes, including changed/deleted operations and deleted files, while
reporting `RestartRequired` for changed entry intent. A retiring mod does not undo committed GameStart changes; restart
without it to remove that intent. Live field reloads continue normally, but a later field edit that would leave a drop
group protected by an active removal with no viable candidates is refused.

**Compatibility and missing mods.** Before publishing an addition Kiln pins its registry kind, stable GUID and terminal game
base in the install-wide, Kiln-owned `kiln/data/registry/entry-compat.json`, keyed by mod id and Kiln entry id (one record across
worlds). This is separate from per-build content caches and the game's own files/saves. Reusing an id with another structural
base is refused; choose a new Kiln id instead. Corrupt/unwritable records fail closed per affected entry and are not pruned when
a mod disappears. Definitions saved through Kiln.Content retain flattened canonical game bases and normalized effective fields.
Without the adding mod, MissingContent restores the same item identity and shape without requiring missing clone ancestors;
placeholder replay adds no recipes or distribution. Returning mods adopt that identity. Restart without a removing mod restores
the game's original visibility; no save tombstone or equipment/storage migration is written.

The report's added/removed rows include Kiln ids and author/source provenance. `pendingWorld` is intent, not a claim that native
bytes changed; `registered`, `hidden` and `disabled` describe reconciled world outcomes. Refused entries are not also counted as successful
additions/removals. Hidden recipes use compared store layers; failed/foreign visibility writes refuse the related removal
rather than claiming an unapplied hide. Dedicated-server and synthetic-memory tests do not establish real-client menu behavior,
already-equipped save round trips or multiplayer parity. Existing content synchronization/look-alike rules still apply; vanilla
clients do not acquire Kiln registry views merely by joining a Kiln server.

### Explicit changes and ready

Inside a loaded mod callback on the active world's thread:

```csharp
var change = Kiln.Registry.Registry.Change(view =>
    view.Items["game:material_t1_wood_logs"].MaxStack = 500);
if (!change.Result.Ok) Log.Warn(change.Result.Reason ?? "registry change not applied");
// Keep the owner-bound handle if needed. Revert recomposes the remaining layers, not an old memory snapshot.
var reverted = change.Revert();
if (!reverted.Ok) Log.Warn(reverted.Reason ?? "registry revert not applied");
```

Inside `Registry.Change`, `Final(...)` is an ordinary Alter edit in that one transaction; it does not schedule a
separate all-mod Final pass. The transaction still applies in commit order over earlier retained changes.

`RegistryChangeResult` copies outcome, accepted/refused/skipped counts, world token, generation and reason. `Ok` means
Applied or Reverted with no refused edits or skipped foreign bytes. PendingWorld/PendingResource do not claim bytes were
changed. RestartRequired, WorldRequired, Refused, Skipped and Unverified stay distinct. A change is not disposable:
only explicit `Revert()` on the tick thread or owner retirement removes its layer, never leaving a scope. Process ModLoad layers persist across worlds; world-bound layers expire with
their world. Already committed GameStart layers stay until process restart, including through a runtime hot reload.

Subscribe to `Registry.Ready` in `OnLoad` for copied stage/live ready notifications; the Host removes the handler at unload.
Its `RegistryReady.Phase` uses `RegistryReadyPhase.Start` or `WorldLoad`, not the author's `RegistryPhase` passes.
Notifications are posted through the owner's scheduler, never run inline on a native loader thread or inside a commit.
A late subscriber likewise receives the last snapshot on its next tick. This event reports readiness/generation, not proof that
every field in a transaction applied: inspect results and the registry report. `Registry.Available` checks the transport;
per-field capability rows still decide whether a field is mapped.

### Offline authoring tests

`KilnHarness.Registry.Seed(registry, id, field, valueJson)` supplies copied JSON. `Author(mod, point, phase, world)` invokes
one loaded instance's OnRegistry pass and returns copied IR (or null for no edits). Contexts expire before it returns.
This is a recording-only fake: seed the next pass explicitly. It does not simulate live application, pass composition,
mapping proof or automatic scheduling, and leaves native registry availability false. Host transport/lifecycle tests and
native backend tests cover those separate paths; offline authoring is not real-game byte proof.

## Capabilities

At resources-ready, the registry stage loads the regmap and reports `registry.<name>` for each registry and
`registry.<name>.<field>` for each schema field. A field is available when its mapping is verified for the running
executable and the field applies to this side. A registry is available when at least one of its fields is available.
Check the field you need with `Capabilities.Is("registry.items.power")` or use `Capabilities.Get` to read its reason
and detail. The detail includes the field's `applies` value even when the field is unavailable. The host resolves
manifest features before the stage, so use these rows in runtime checks after resources-ready.
[Capability states and reasons](../../services/caps.md#rows-added-in-dev31)

Capability names preserve the field's case and dots and omit the `[]` list marker. Use the schema path in edits and
the capability name when querying `Capabilities`.

| Capability name | Schema field path |
|---|---|
| `registry.recipes.inputs.item` | `recipes.inputs[].item` |
| `registry.recipes.inputs.category` | `recipes.inputs[].category` |
| `registry.recipes.inputs.count` | `recipes.inputs[].count` |
| `registry.recipes.outputs.item` | `recipes.outputs[].item` |
| `registry.recipes.outputs.count` | `recipes.outputs[].count` |
| `registry.drops.entries.item` | `drops.entries[].item` |
| `registry.drops.entries.count.min` | `drops.entries[].count.min` |
| `registry.drops.entries.count.max` | `drops.entries[].count.max` |
| `registry.drops.entries.chance` | `drops.entries[].chance` |
| `registry.drops.entries.weight` | `drops.entries[].weight` |
| `registry.balance.buildzoneSizesPerAltarLevel.x` | `balance.buildzoneSizesPerAltarLevel[].x` |
| `registry.balance.buildzoneSizesPerAltarLevel.y` | `balance.buildzoneSizesPerAltarLevel[].y` |
| `registry.balance.buildzoneSizesPerAltarLevel.z` | `balance.buildzoneSizesPerAltarLevel[].z` |

## When an edit takes effect: applies

Every field of every registry says when an edit of it takes effect, in the schema, on the C# property
(`[RegistryField("items.maxStack", RegistryApplies.WorldLoad)]`), in `RegistrySchema.Find(...).Applies` and on its page:

| Applies | Means | Typical fields |
|---|---|---|
| `now` | at the next use (the next craft, roll, hit, payment) | gear numbers, recipe inputs, loot stack sizes, drop chances |
| `worldLoad` | when a world loads: in place before the world's save is read | `items.maxStack` (a lower value cuts stacks in that save) |
| `restart` | after a game restart: the game reads it once at start | the balance table |

A field whose read time Kiln has not traced yet ships with the more cautious value (worldLoad or restart, never now) and an
unproven mapping (refused per entry, with a reason, until a Kiln update proves it); no field is in that state in this release.
Two fields stay off on the current build, each for a proven reason, and every edit of them is refused with it:
`items.fuel.burnTime` (no game code reads an item's burn time on this build, so an edit would change nothing) and
`recipes.station` (read-only; a station is a stations id, which comes with the stations registry). Each page's Mapping
column says which fields this Kiln maps, which are unproven and why.

### What a joined client sees

`applies` describes when the **local game** consumes a value. The separate, per-field `joinedClient` marker in the
schema, `RegistrySchema.Find(...).JoinedClient`, the generated property's `RegistryFieldAttribute.JoinedClient` and
each generated page's **Joined client** column describe the multiplayer path (since 0.2.0-dev.31.17, mod API 0.2.24). Keeping it separate is additive:
`RegistryApplies` and its three existing values keep their meaning, and no existing property constructor changes.

| Joined client | Server edit on an already joined Kiln client |
|---|---|
| `now` | ContentSync transfers the effective item/recipe layer; the client uses it at the next use after receipt. Not instantaneous: generation/changed-count updates are checked periodically, and value-only changes are exported at least every 10 seconds while sync is enabled. |
| `worldLoad` | ContentSync transfers the layer, but a consumer that read it at world load (notably stack clamping) is not replayed. The next client world load consumes it. |
| `restart` | Not transferred by ContentSync. Both sides need the same boot values at process start; the join compares the resolved values when both peers send them ([network.md](../../network.md#registry-boot-values-and-order)). A rejoin alone is not enough. |
| `serverOnly` | The client needs no copy for this field; server outcomes apply. There is no client-held value to refresh. |
| `localOnly` | The server does not author/sync this field (for example client-side recipe unlock rules). The client's own mod authors it locally. |
| `rejoin` | A client-held field would refresh only at the next join, not during the current connection. No shipped registry field currently uses this marker. |
| `unknown` | Read timing/mapping is unproven or missing; the edit is refused, not promised to become visible on rejoin. |

These are field contracts, not proof of a specific UI refreshing or an equipped/save-backed instance being rebuilt.
ContentSync must be enabled on both sides; vanilla clients keep their own UI/prediction numbers. Skills node costs are
**not** registry fields; their independently built client tree can retain an old cost until rejoining with matching
files ([data-pack caveat](../../services/content.md#multiplayer-registry-and-skills-edits)).

### Registry-only packs: choosing `clients`

`clients: "kiln"` always bars vanilla clients by the normal join policy, even if every edit is server-only. For a
**data-only pack with only a `registry` section**, choose the strictest requirement of the fields it edits:

| Fields / operation | `clients` | Why |
|---|---|---|
| `loot`, `drops`; any `serverOnly` field (including the read-only balance gear constants, which cannot be edited) | `none` | Only the server needs the values. |
| Synced `items` / `recipes` fields (`now` or `worldLoad`) | `kiln` for matching UI/prediction; `none` deliberately permits vanilla UI/prediction | Kiln clients get server layers; vanilla clients do not. |
| Client-needed `balance` fields (`restart`) | `mod`, with `side: "both"` | Each process must bake its own matching value/settings and load order; `kiln` alone cannot transfer it. |
| Client-only fields (`localOnly`, currently `recipes.unlock`) | `mod`, with `side: "both"` | The client's own mod supplies them; a dedicated server refuses client-only edits. |
| Item/recipe entry add/remove | At least `kiln`; `mod` for custom assets or other client-local content | Definitions and visibility are not just scalar server rules. Existing content/look-alike rules still apply. Loot removal alone remains server-only (`none`). |
| Unproven/unknown or raw fields | Inspect the field/refusal first | No automatic safety claim; lint does not guess. |

For `items`, `rolled`, `socket` and the read-only `hasDamage` / `hasArmor` are `serverOnly` under the current
mapping (`staminaCost` follows ContentSync like `manaCost`: the server charges it, but a client's tooltip and prediction
read their own copy); `block`, `comfort` and `fuel.burnTime` are `unknown` until their read timing is proven. Other item
fields follow ContentSync. `recipes.station` is read-only/server-only; the remaining recipe fields sync except
`unlock`. `loot`/`drops` never require a client copy. The generated pages are the per-field reference.

`kiln lint` / `kiln-manage lint` reports `KL213` for a registry-only pack whose effective `clients` requirement differs
from those fields, with a suggested value. It does not silently change admission, lower a mixed/code mod's policy, or
infer that raw/unknown fields are safe. A mixed pack follows the requirements of **all** its content, not just registry
edits. A server operator may intentionally admit vanilla clients for synced existing-item/recipe tweaks; retain that
choice knowingly rather than assuming vanilla clients acquire the server's numbers.

**Lists.** A list of plain values (`items.perks`) takes clear, append, remove (by value) and replace; a list of entries
with fields (`recipes.inputs`) takes clear, append (a new entry, then its fields) and remove (the entries a condition
picks, named by their values), so two mods' appends and removals stack. A list of fixed length (`balance.xpPerLevel`,
exactly 5; `balance.altarsPerFlameLevel`, one value per flame level) is edited one element at a time
(`xpPerLevel[2]`). A reference that may be absent (a recipe input's item or category) can be cleared.

### What a reload changes
A `reload <id>` (or an `unload` and a `load`) of a mod with registry edits changes what its `applies` allows now, and says
what waits for the next start (since 0.2.0-dev.31.17). The rest of the mod's `kiln.mod.json`: [architecture.md](../../architecture.md#what-reload-id-covers).

| The mod's edit | After the reload |
|---|---|
| a data pack's `registry` section, a field that applies `now` or `worldLoad` | composed again with every other mod's, in load order, at kiln.content's next Pre; registry.json has the new chain |
| ... a field that applies `restart` | the start's value stays; the reloaded value applies at the next start. registry.json: `RestartRequired: <registry>[<id>].<field> is read at game start; the reloaded value applies at the next start` |
| ... a field the start never read (no mod edited it at the start) | not applied: `RestartRequired: <registry>.<field>: the base was read at start; this edit was not applied` |
| ... an entry add or remove the start did not have, or one it changed or dropped | the start's entry operation stays: `RestartRequired: <registry> entry intent changed after GameStart; ...` |
| ... a section of a mod that had none at the start | not applied: `RestartRequired: a registry section added after the start needs a restart; ...` |
| C# `HotReload` / `ModLoad` / `WorldLoad` | runs again (see the entry points above); a restart field or a field the start never read is refused for that pass with `RestartRequired: ...`, like a live edit |
| C# `GameStart` only | does not run again: the start's edits stay. When the reloaded code differs from the code the start ran, the reload reply and kiln.log say `RestartRequired: <id>'s OnRegistry runs at GameStart only ([RegistryEntryPoints] has no HotReload or ModLoad): ...` |

Every edit that waits starts with the stable token `RestartRequired: ` in registry.json's `refused` list, with the mod and
the place (`registry`, `id`, `field`), and `counts.restartRequired` counts them all (also those the list's cap leaves
out). Where they show:
- the `kiln/control` reply of `reload <id>`: `reloaded <id> <version>; registry: 2 edits need a restart: <mod>
  balance.levelCap, <mod> items[game:wood].levels.min (kiln/state/registry.json has each reason)`, and `data.registry`
  (`restartRequired`, `edits[]`) for tools such as the Web Admin mod ([architecture.md](../../architecture.md#what-reload-id-covers));
- `kilnctl status`: `registry  2 registry edits wait for a restart: ...` ([kilnctl.md](../../kilnctl.md));
- the launcher's Conflicts view, first, as "Waits for a restart";
- kiln.log, with each refused row.

A restart (of the game, or of the server) applies them; nothing else does. A pack that throws while it applies leaves
none of its mod's new registry section behind ([services/content.md](../../services/content.md#when-a-reload-fails-partway-020-dev3113)).

## Stages and conflicts

At each start Kiln runs three stages, each across **all** mods (in the load order players see) before the next: every mod's
`add`, then every mod's `alter`, then every mod's `final`.

- Later in load order wins, and the report says so ("overridden").
- Relative changes stack ("composed": x4 then x2 = x8).
- `exclusive` on an edit keeps the strict rule: another mod's edit of the same field is a conflict and the later one is
  refused whole.
- `yield` applies an edit only where no earlier writer set the field.
- `final` follows alter's rules; a mod's final sees the result of add and alter, not other mods' finals.
- Registry edits layer on top of Kiln's older paths (boot resource patches, data packs) and on top of values another
  loader wrote into the game's archive before Kiln, and are reported with them. Old mods are never refused because of a
  registry edit. Conflicts inside the registry and with Kiln's older paths are found for certain; other loaders' DLL
  patches and hooks are best-effort and labelled so.
- `Content.*` changes made by code (`Content.Apply`, `ContentEntry.Set`, another loader's change through Kiln's content
  service) sit **above** registry edits and win, as they win over data packs. A relative op such code gives (`mul`, `add`,
  `min`, `max`, `raiseOnly`, `lowerOnly`) is computed by kiln.content on the **game's original value**, as for any data pack
  ([services/content.md](../../services/content.md#data-packs)), never on what a registry edit made of it: BigStacks'
  `{"mul": 4, "max": 9999, "raiseOnly": true}` gives wood logs 250 x 4 = 1000 even where a registry pack made them 2000
  (`raiseOnly` means "never below the game's value", not "never below the value under the layer"). That has been so since
  registry edits exist and stays so, since changing it would change what existing mod sets get. registry.json says so
  (0.2.0-dev.31.18): the code writer is the field's winner and one certain `overridden` line names it, the registry edit it
  replaced and both values ("... (a Content change made by code) sets 1000 over registry-mixed-test's 2000 (code wins over
  registry edits; a relative change made through Content is computed on the game's value, 250, not on the registry's)").
  A mod that wants its change to stack on the registry's value makes it a registry edit (a relative op there composes on
  the value below it).

### Who else wrote a field

Every field row in `kiln/state/registry.json` (and the launcher's **Conflicts** page) lists its writers, lowest first, each
with a certainty:

| Writer | Certainty | Where Kiln learns it |
|---|---|---|
| Kiln's older paths (boot resource patches, data packs, `Content.*` code) and registry edits | `certain` | Kiln applies them itself |
| A change made through Kiln's content service by code, also another loader's (an EMF DLL mod's `set_item_max_stack`) | `certain` | named `<who>: <Kiln place>`, `<who>` being the mod, else the loader's own name for its mod (`emf:big_stacks`); a label the mod gave its change (`Content.Apply(json, "bigstacks x4")`) stays as it wrote it (0.2.0-dev.31.18; before, such a row named the game's item and member, `owner 67: items Material_T1_Wood_Logs.maxStackSize`) |
| A ShroudForge or EML package that wrote the entry (or, from EML's own pass, the field) before Kiln (`archiveWriter`, who `shroudforge:<package>` or `eml:<package>`) | `named` | kiln-eml's change journal of the start's archive writer (below) |
| Something outside Kiln changed the game's value (`drift`, who `outside Kiln (best effort; suspects: ...)`) | `bestEffort` | the drift probe at each world load and pack reload (below) |

**Archive-writer packages.** kiln-eml writes a change journal of its startup pass beside its ownership receipt
(`kiln/eml/ownership/<exe name>.change-journal.json`): which package wrote or created which of the game's resources, in
the order the packages ran. Two kinds of start write one:

- **EML mods** (kiln-eml's own EML pass, a start with EML mods and without Kiln's interpreter for ShroudForge mods; next
  release): per EML mod, the resources it wrote and the **member paths** it wrote in each, at their places in the data
  the game reads (format 2). A start that changed nothing keeps the journal of the start that published.
- **Kiln's interpreter for ShroudForge mods** (kiln mode): per ShroudForge package (and the EML packages its pass ran),
  the resources it wrote or created (format 1, per resource).

The registry stage reads it once per start, only when it belongs to this start's archive (the generation kiln-eml
published or found live this start; a journal left by an earlier start is `stale` and names nobody), and names each
package where it wrote: a row below every Kiln writer, valued at the base (what the package wrote is the archive Kiln
reads), and a conflict line such as `items[game:material_stone].maxStack: betterstacks (EML package) wrote this field in
the game's archive before Kiln; bigmod's edit wins (D1): 25 -> 100`. Registry edits always win over these values, and
relative edits compose on them. `exclusive` and `yield` do not count a package as an earlier writer (its value is the
base).

- **Per field (EML mods).** A mod is named on the Kiln fields its member paths hold or lie in: a mod that set an item's
  `maxStackSize` is named on `items.maxStack` only; one that wrote `damageSetup.dmgMod` on `items.power` (the field
  Kiln computes from it); one that replaced a struct or a list on every field inside it; one that replaced the resource
  whole on every field of the entry. A recipe or a loot entry (an element of the recipe list or the loot list) is named by
  its element (`recipes[37].craftingDuration` names `recipes.duration` of the 38th recipe; a recipe put in place whole
  names that recipe on every field; the recipe list replaced whole names every recipe). Inserting, removing or clearing
  elements of a list names no other element. A mod that wrote a member before another mod replaced the value holding it
  (a struct, an element, the resource's data) stays named there, since the new value can be a copy of the old one; its
  write moves with the member of the copy (a recipe inserted in front of it afterwards moves it too). A
  mod's own entry module (`src/mod.lua`) writes as that mod, even when another mod requires it first. The field row's
  writer has `"named": "field"`.
- **Per entry (ShroudForge packages, created entries).** The kiln-mode journal names a package per **entry**: a package
  that wrote one member of an item is listed on every field of that item a mod edits (`"named": "entry"`). Only **items,
  drops and balance** are named from it: their entries are each one whole resource, which is what that journal lists; a
  recipe or a loot entry is an element of a resource its whole registry shares and that journal does not say which
  element a package wrote, so no ShroudForge package is named on recipes or loot (`base.journal.text` says so). The
  balance table is one entry: a package that wrote any balance value is listed on every balance field a mod edits. A
  package that created an entry is named on the whole entry.
- "Wrote" means written, not changed: a package that set a value to what it already was is listed too. The journal can
  name a package that did not change a field; it never misses one that did, within its bounds (4,096 rows per package,
  32,768 in all, 16,384 member paths per resource and 8 MiB of paths in all; `base.journal.truncated` says when it ran
  out of rows; more paths than that name the whole resource). So a package's row has no `op` (the journal does not say what the package did to the
  field; its `value` is the archive's, the base) and a package is never a field's `winner`: with no Kiln writer left, the
  archive's value stands as the base.
- `base.journal` in `registry.json` says what happened: `read` (with how many packages and entries), `none`, `stale` (a
  journal left by an earlier start, which names nobody) or `unreadable`, with a sentence.
- **No names without a journal.** Hosted or installed ShroudForge (the default for ShroudForge mods) runs its pregame pass
  in its own runtime (Kiln's bundled fork, or the player's copy): kiln-eml stands aside and sees none of its writes, and
  ShroudForge's own record of the pass (`shroudforge/state.json` `assets`) lists the asset mods it applied, not which
  resource each wrote, and names no generation Kiln could check it against. Naming them needs a per-package write record
  in ShroudForge's own pass (as kiln-eml keeps for EML's), which is ShroudForge-side work: its packages are not named. Other loaders (EML's own `dbghelp.dll`/`dinput8.dll`, other DLL
  engines) keep none either. What they wrote is still in the base, and registry edits still win over it, but no row names
  them; the report says so (`base.journal.state` `none`). (The first start after a Kiln update runs the EML mods again,
  as kiln-eml's change cache belongs to one kiln-eml build, so an EML start gets its journal then.)
- **Who published the base** (0.2.0-dev.31.18; before, `base.writer` said `none` on every start but a kiln-mode one, also where
  kiln-eml or hosted ShroudForge had patched the archive): `base.writer` is this start's archive writer: `kiln-eml` (its EML
  pass, or Kiln's interpreter in kiln mode), `shroudforge-hosted` or `shroudforge-installed` (ShroudForge reserved the startup
  data writer), `external` (another loader's proxy ran instead of kiln-eml, or the game data differs from kiln-eml's vanilla
  backup and is not its output) or `none` (the game's data as shipped, as far as kiln-eml can tell). `base.archive` says what
  that rests on: kiln-eml's state of the start (`phase`, `patched`, `cached`, `restored`, `mods`), the name of kiln-eml's
  ownership receipt (`receipt`, `kiln/eml/ownership/<stem>.json`) or of ShroudForge's record of its pass (`record`,
  `<stem>.kfc.prepared.json`; named, never read), and a sentence. kilnctl's values diff still takes only `none` as the game's
  own data ([regmap/README.md](../../../../regmap/README.md)).

**The drift probe (best effort).** At each world load and each pack reload Kiln compares, for every field it writes live,
the game's value just before Kiln's first write in that world with what the stage read at start plus the writers Kiln knows
below the packs. When they differ, something outside Kiln changed the value (another DLL engine, a hook, a memory write):
the row says so and names the suspects Kiln can see, the modules that patched game code before Kiln hooked it (the hook map,
`kiln/state/hooks.json`) and hosted ShroudForge when it runs. A slot another writer changes after Kiln's layer gets the same
row (Kiln's edit is then skipped for that world, as before). The probe never refuses anything. It runs whenever Kiln
rebuilds `registry.json`'s live rows (at each world load and pack reload, and when a live change or a drop table the game
loads later changes them; never per tick) and costs one more read of each field it writes live, about a tenth of what
rebuilding the rows costs anyway.

**Writes after the last report** (0.2.0-dev.31.18; before, they stayed out of `registry.json` until the next pack reload or
world load: the compat rehearsal saw wood logs at 300 there while EMF's big_stacks had made them 1200). Kiln also looks
again between those rebuilds, never per tick: about 2 s after a change through Kiln's content service (code, another
loader's change through Kiln) and every 10 s for a write outside it, comparing each field it writes live with what the last
report saw. When one changed, `registry.json` and the Conflicts view get the field's new writers and result (a code writer
as above; a write outside Kiln as a best-effort row last, with a line: "the game's value changed outside Kiln after Kiln's
last write in this world"); no new generation, no ready event, nothing written into the game. A field that keeps changing
is looked at less often (up to once a minute). kiln.log says so once. A look reads each slot Kiln writes live, at most
1,024 of them a tick (a pack over every entry's stacks or drops writes tens of thousands: its look takes a few dozen ticks),
and rebuilds nothing when nothing changed; when something did, the rebuild costs what a pack reload's report costs. The rebuild itself stays on the world tick; `registry.json`'s JSON and file are made on a thread of Kiln's own, never on the tick (0.2.0-dev.31.19), for this rebuild and every other report after the start: a newer report replaces one not yet written, `kiln.registry`'s report text is the newest report even before its file is written, and a report still waiting for that thread is written before the server or game exits normally (Kiln waits up to 2 s). A write
outside Kiln that comes back to Kiln's value between two looks is not seen. A suspect list is a hint, not proof: a writer that hooks nothing is not
seen, and a field nobody edits through the registry is not checked. Neither is an entry an old boot resource patch (an
`OnBoot` or `resourcePatches` document) writes: the game holds Kiln's own patch there, and many of its paths (a formula's
input, an element of a list of structs, `[*]` over every recipe) have no field row to explain it, so a change there is
never called another loader's.

### Native callback and owner lifetime

`kiln.registry` keeps submitted batches and callbacks under their caller's owner. Revoking that owner removes its queued
batches (including parse refusals) and its stage/ready subscriptions, even before any world exists. Another live owner of
the same mod keeps its own batches. Admission and dispatch also check the core's current owner state, so a retiring
notification still being delivered cannot admit or start another callback into the dead owner.

The stage takes the surviving queued batches at startup. Edits it already took remain until the next process start;
revocation is not a rollback of committed registry layers. A stage callback already in flight may finish, but if its
owner was revoked, that turn's uncommitted edits are dropped and the report names the reason. A fault in either kind of
native callback removes that subscription; a faulted stage turn is dropped as well.

Unsubscribe and revoke never wait for callback quiescence. A callback whose dispatch already started on another thread
may still finish, so its native function and user state must stay valid until that dispatch has completed. Ready dispatch
rechecks each subscription, including when an earlier callback in the same START or WORLDLOAD event unsubscribes or
revokes a later one. These lifetime rules add no members to `kiln.registry`, which stays 88 bytes. Live transactions
use the separate 56-byte `kiln.registry.live` v1 table. The managed Host owns these subscriptions and their retirement.

### Live values in the report

After world load or a pack reload, `kiln/state/registry.json` shows the live `result` and writer chain for recipe inputs
and outputs, drop-table entries, item perks, and power and armor. The chain shows older packs below the registry edit.
`composedOn` names the pack whose value a relative edit used, and `overridden` names the pack an absolute edit replaced.
Drop-table rows refresh within the current generation when the world acquires their tables.

List values are arrays of entries in game order. The report keeps the first 64 entries per array and includes the omitted
count in `resultEntriesLeftOut`, `base.entriesLeftOut`, or the writer's `valueEntriesLeftOut`, `listEntriesLeftOut`, or
`opEntriesLeftOut` as applicable. Formula rows keep the numeric `result` and include `formulaInputs` in evaluation order.
Power lists the damage factor, attack speed factor, and minimum level. Armor lists the armor share and minimum level.

## Plans and rollback

Each start's stage writes its **plan**: `<game>/kiln/data/content/registry/plans/<sha256>.json` (format 2), named by the
SHA-256 of its content. It holds what the stage composed: each mod with its version and its lowered edits (data sections,
batches submitted before the stage and its `GameStart` `OnRegistry` turns, in the order they composed, as IR), the values
they resolved to (`values`, `"<registry>[<id>].<field>"`, restart fields as the boot back-end wrote them; live fields as the
stage composed them over the base), the entries added and removed, and the game build, side and Kiln version. The same mods
with the same edits give the same plan.

**Which plans are kept** (0.2.0-dev.31.18; before, none was ever removed, so the folder grew by one file for every changed set of
mods and settings). At the end of a successful start Kiln keeps the last good plan and the good plan before it, the plan the
last world ran on, this start's plan and its `previous`, every plan a pending `rollback.json` names (its `plan` and `from`,
read again at that moment), and the newest 10 other plans (by the file's last write: a start on the same edits writes its plan
again), and removes every other `plans/<sha256>.json`; kiln.log says how many (`registry: plans: removed ...`). Only plan files
are removed: `plans/index.json`, a kept `plans/index.unreadable.json` and any other file stay. Nothing is removed by a start
that failed or whose state is not known: one whose stage had no base to read, whose rollback request was refused (or cannot be
read), whose `plans/index.json` could not be read, or that wrote no plan (a rolled-back start runs on a plan that is already
there and writes none, so it removes none either). `kilnctl registry rollback --to <hash>` can name
only a plan that is still there (a removed one is refused: "not in kiln/data/content/registry/plans").

**The last good plan** is the last one a world started on without a refused edit (`plans/index.json` keeps it, the good one
before it, the plan the last world ran on, and the highest max stack a world started with per item); a start whose stage had
no base to read applied nothing and is never good. registry.json `plan.previous` names the last good plan that is not this
start's: what a rollback goes back to. On a rolled-back start it is the `previous` that plan was written with. An index that
cannot be read is kept as `plans/index.unreadable.json` with a kiln.log warning; that start has no previous plan, and since
the highest stacks are unknown Kiln's stack guard goes on. An index entry that names no plan hash is ignored.

**Rollback.** `kilnctl registry rollback <server instance>` (a client: `--dir <game folder>`, or the client Steam knows with
`--real`) says what changes back, then writes `kiln/data/content/registry/rollback.json`. From the next start on, until
`kilnctl registry rollback --clear`:
- the stage runs the plan's edits instead of the mods' current registry edits: data sections, queued batches and `GameStart`
  callbacks (those are not called; the plan holds their edits). Old paths (packs, boot patches, `OnBoot`, hooks) run as always;
- registry.json `rollback` says `applied`, lists every value that changed back (`changedBack`: field, from, to; also in
  kiln.log) and the mods with registry edits the plan does not hold (`leftOut`: not applied while the rollback holds);
- `ModLoad`, `WorldLoad`, `HotReload` passes and `Registry.Change` of the plan's mods still run as the mods make them (they
  are not part of a plan); a left-out mod's live changes are `RestartRequired`, and so is a pack reload's changed `registry`
  section.

The plan is **refused whole**, and the start runs the mods' current edits (registry.json `rollback.state` `refused`, with the
reason, and a kiln.log warning), when the request cannot be read, the plan file is missing, was written by an older Kiln
(format 1), or does not match its name, it is the other side's, one of its mods is not loaded (`<mod> is gone`) or not at
its version (`<mod> changed (1.0.0, now 1.1.0)`), or one of its edits is refused on this start (a field this build does not
map, an entry this build does not have, a save check below). The save checks judge every value of the plan twice: against
the value without the registry (as for any edit) and against the plan it rolls away from (the request's `from`; without one,
the plan the last world ran on), since a world ran on those values and its saves can hold them. A narrower level range or a
shorter build-zone list than that plan's refuses the rollback whole; a lower max stack needs the stack guard at the stage. It
is never half-applied. Refusals that only a world's load can make (an entry that world lacks) stay per entry, as on any start.
A plan made on another game build may start when every edit of it maps there (`rollback.madeOnExe` says so); schema
migrations apply to it like to any edit.

A rollback **never touches saves**: it writes one file beside the plans. When it gives an item a lower max stack than a world
already started with, or than the value under it, Kiln's stack guard keeps the bigger stacks a save holds whole; where the guard is not available the
rollback is refused. `kilnctl registry status` shows the plan of the last start, the previous one and any rollback. The
launcher's Conflicts view has the same as its **Registry plan** card (since 0.2.0-dev.31.17): the plans, the last start's
rollback and a waiting request; **Roll back** shows the dry run's changes first and writes the request only on confirm,
**Clear** removes it ([launcher/registry-conflicts.md](../../launcher/registry-conflicts.md)).

## Edits that change existing saves

A registry edit changes the game's data, never a save. Some fields are read against what a save holds, so Kiln checks each
edit of them against the value without it (the game's, or what an older path wrote) and decides:

| Class | Edit | What it would do to saves | Kiln |
|---|---|---|---|
| `stackCut` | `items.maxStack` lower | the game deletes the part of a saved stack above the new cap at its next move | **guard**: applied, the stack guard keeps such stacks whole (new stacks hold at most the new cap); refused for that entry where the guard is not available |
| `stackCut` | any start (or rollback) giving an item a lower max stack than a world already started with (`plans/index.json` `stacks`: the highest per item, never lowered) | the same, for stacks such a world saved | **guard**; where the guard is not available, a warning (nothing is left to refuse), and a rollback is refused whole |
| `levelRange` | `items.levels.max` lower, `items.levels.min` higher | a world load clamps saved copies into the range and the save keeps the clamped level | **refused** for that entry |
| `listShape` | `balance.buildzoneSizesPerAltarLevel` shorter | a saved altar at a higher level has no build-zone size | **refused** |
| `perkSlots` | `items.perks`: a filled slot changed or emptied | saved copies keep rarity and unlocked count, and have the slot's new perk (perks are read from the item's slots while it is used) | **warning**, applied |
| `progressionCap` | `balance.levelCap`, `balance.skillPointsPerLevel` lower | saved characters keep their level and spent points; what the game does past the new cap is not traced | **warning**, applied |
| `experienceCurve` | `balance.xpPerLevel`, `balance.xpCurve.*` changed | saved characters keep their experience; a level whose threshold moved is not recomputed by Kiln | **warning**, applied |
| `baseLimits` | `balance.altarsPerFlameLevel`, `waterSourcesPerFlameLevel`, `buildzoneSizesPerAltarLevel` values lower | saved bases keep what they hold above the new limit; what the game does with it is not traced | **warning**, applied |
| `durability` | `balance.durability.*`, `balance.rarityDurability.*` | each saved copy keeps its share of durability; max and current follow the new value at load | **reported**, applied |
| (none) | everything else: recipes, loot, drops; an item's rarity, rolled, socket, power, speed, damage, armour, costs; the balance table's gear, unlock, socket chance and fog numbers | read at use, or only for new copies | applied |

Refusals are in registry.json `refused` and kiln.log (`saves: ...; this edit was not applied`), the rest in registry.json
`saves` (field, class, decision, text) and kiln.log (`registry: saves: ...`). Restart fields are checked at the stage, live
fields at each write over the value under them (an older pack's included), so a ModLoad, WorldLoad or Manual edit is checked
too. A new entry's first values (its add's `fields`, over the entry it is like) are not checked: no save holds a copy of it
yet; its later edits are. The value under an edit is the game's (or an older path's), not what an earlier start gave it, so
a mod that lowers its own earlier raise of `items.levels.max` is not refused (without the edit the game's value applies,
lower still); a start whose level range is narrower, or whose build-zone list is shorter, than the plan the last world ran on
gets a `levelRange` or `listShape` warning in `saves` instead. A rollback is judged against the plan it rolls away from and
refused whole ("Plans and rollback"). Balance fields stay restart-only.

## Ids

Every entry has a Kiln id: `game:<stable name>` for the game's entries, `<mod id>:<name>` for a mod's. The grammar is
`<namespace>:<name>` with the namespace `[a-z0-9][a-z0-9_.-]*`, the name `[a-z0-9_.-]+`, and for a game entry an optional
clash suffix `~<8 to 32 hex digits>`; the mod ids `game` and `kiln` are reserved. A debug name with other characters
(spaces, apostrophes, letters outside ASCII) has each of them as `_` and is pinned. Ids are lower case,
never reused, and keep working across game updates: Kiln keeps an alias table per game build (keyed by the entry's GUID)
that follows renames, so a game entry keeps the id it had when the game renames it. A game entry a game update removes
refuses every edit that names it ("removed by game build ..."). The old debug-name selectors of data packs keep working.

How a game entry's stable name is made (each registry's page repeats its own rule):

| Registry | Stable name | On game build 1287369 (server) |
|---|---|---|
| items | the item's debug name in lower case; `item_<item id, 8 hex>` when it has none | 4,268 items: no empty name, no clash (also none ignoring case); levels run to 105 (gems), so the formulas take the field's whole 1..105 |
| recipes | the recipe's debug name in lower case; `recipe_<recipe id, 8 hex>` when it has none (the name kiln.content gives such a recipe today) | 2,542 recipes: no empty name, no clash; 703 share a name with an item (different registries: no clash) |
| loot | the id of its item | 4,213 entries |
| drops | see "Drop tables" below | 420 tables |
| balance | `game:balance` (one entry) | 1 |

**Two entries with one name.** The entry that held a stable name first keeps it for good; a later entry whose name is the
same (also when the two differ only in case) gets `game:<name>~<first 8 hex digits of its GUID>` (more digits if those
are taken as well), and the plain name as a selector means only its holder. "First" on the first game build Kiln maps is
the order of the game's archive (the order the boot snapshot and boot resource patches see, which already keep the
first and report the later one as a duplicate); from then on Kiln's alias table records every suffix it gave, so a later
game build never moves a name to another entry. Templates are the case today: 12,269 templates on 12,229 names on build
1287369, 40 later entries with a name taken before them plus 1 that differs only in case, on 39 names. They get the suffix
when a templates registry exists; no R1 registry has a clash on this build.

**Drop tables** have no names in the game. A table is named after the templates that use it (their `DefaultInventory`
component): `game:` + the first of their stable names in ordinal order, in lower case
(`game:lootcontainer_enemy_scavenger_melee01` for the scavenger's table that 12 templates use). A table no template uses is
`game:drops_<first 8 hex digits of its GUID>`. Two tables can only get one name if a template used two tables; then the
clash rule above applies. On game build 1287369 (client data): 837 templates use 357 distinct tables (242 tables by one
template, 115 by several, up to 68), the rule gives no clash, and the server's 420 tables leave about 63 unused by any
template, which take the `drops_` form. The table's GUID also selects it, as data packs' `drops` section does today.

## Older paths as front-ends (0.2.0-dev.32)

An older path is a **front-end** of a registry where Kiln writes what it says through the registry's own mapping and
back-end, and only where that gives the game the same bytes. The mod keeps its file and its behaviour; what changes is that
its values pass through the proven mapping (after a game update, a Kiln update of the mapping carries them) and that
registry.json shows them.

| Older path | Registry | Front-end | Where not |
|---|---|---|---|
| a data pack's `gearRules` (boot values) | `balance`: `rarityDurability.*`, `socketChance.*`, `durability.*`, `unlockCost.*` (19 fields of the same names) | yes: the registry's boot back-end writes the file's values, at every start compared byte for byte with its resource patch | a value of a field not proven on the running build, an operation whose `min` is above its `max`, or copies that differ: the resource patch writes it, as before (registry.json `frontEnds` says why) |
| a data pack's `gear` section | `items`: every gear key is the items field of the same meaning (`power`, `speed`, `damage.*`, `armor.*`, `block`, `staminaCost`, `rarity`, `levels.*`, `rolled`, `perks`, `socket`, `maxStack`) | no: it stays a data pack | one registry edit per key does not always mean the same: [content.md](../../services/content.md#gear-values-and-the-items-registry-020-dev32) lists the cases; a registry section can say the same as a rule (the lab gives the same item bytes) |
| `Content.Gear(...)` in C# | `items` | no: it is code (`Content.*`), which sits above registry edits by design | |

A front-end keeps the older path's place in the composition: below every registry edit, an earlier writer for `exclusive` and
`yield`, conflicts with the other older paths as before; registry.json names it on each field it writes (`kind: oldPatch`,
`frontEnd: true`) and lists it under `frontEnds` (`state: registry` or `resourcePatch` with a `reason`). It is no registry
edit: the join's registry part (`kiln.registry.boot`) and the `counts.edits` do not count it, so peers of older Kiln
versions compare as before. `<game>/kiln/data/content/registry/front-ends.off` switches the front-ends off for the starts
while it is there.

## Tiers

| Tier | What | Promise |
|---|---|---|
| Stable | registry schemas, Kiln ids and fields, `Kiln.Registry` C# and data sections, typed blocks | survives game updates: Kiln updates the mapping; frozen by API 1.0.0 |
| Advanced | raw reflection paths (`raw` in an edit), resource patches, GameFields/GameTypes, boot.GameData keys | works while the game's names hold; checked and refused per entry, never silent corruption |
| Native | hooks by address, raw memory, native plugins | the mod's own risk |

## Files

| File | What |
|---|---|
| `schema/registry/<registry>.json` | a registry's schema: fields, types, ranges, ops, applies, ids rule (Kiln names only) |
| `schema/registry-schema.schema.json` | the schema of those files |
| `schema/registry/ids.lock.json` | every published registry and field name with its numeric id; never reused |
| `schema/registry/migrations/` | id and field renames a breaking schema version ships (applied to every edit, "Versions" above) |
| `<game>/kiln/data/content/registry/plans/<sha256>.json`, `plans/index.json` | each start's plan (the ones still needed and the newest 10 others are kept: "Which plans are kept"); the last good plan and the plan the last world ran on ("Plans and rollback"); `plans/index.unreadable.json`: an index that could not be read, kept |
| `<game>/kiln/data/content/registry/rollback.json` | a rollback request (`kilnctl registry rollback`), kept until `--clear` |
| `<game>/kiln/data/content/registry/front-ends.off` | while it is there, the older paths stay off the registry ("Older paths as front-ends") |
| `schema/registry-section.schema.json` | the data section's schema (generated) |
| `regmap/` | how each field maps onto the game, per game build (Kiln's part: mods never read it) |
| `tools/registrygen/registrygen.py` | the generator; `--check` fails on stale output, `lock` and `retire` keep the ids |
