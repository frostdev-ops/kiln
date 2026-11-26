# Stable data registries

Kiln's registry schema uses Kiln ids and fields, not the game's functions, types or memory. See the
[registry guide](../modding/registries/README.md) for fields, ordering, ids and tiers.

## Native stage: kiln.registry v1

The 88-byte table in `native/core/abi/services/kiln_registry.h` supplies schemas, field metadata, reads/selectors,
owner-bound staged IR, stage callbacks, ready notifications and the current report. The stage runs every mod's add,
then alter, then final, in Host load order. Successful callback turns are retained for later live recomposition; no
native callback or mod assembly is invoked again by a pack reload.

The existing `schema_json` member also exposes each field's additive `joinedClient` marker, separate from `applies`
([visibility values](../modding/registries/README.md#what-a-joined-client-sees)). After start it includes `bootSync` with
full SHA-256 `values` and `order` hashes of client-needed boot registry edits (left out when this start could not apply
them as asked: [join compatibility](../network.md#registry-boot-values-and-order)). The same immutable snapshot is in the
registry report; it is not affected by report caps or later live layers. The Host reads the small schema snapshot for
join fingerprints rather than loading the entire writer report. These are additive JSON members; the table and
field-info ABI layouts are unchanged. [Join compatibility](../network.md#registry-boot-values-and-order)

Owner revocation removes queued work and callbacks. Already-taken start-time edits remain until the next start.
Callbacks already in flight are not waited for; their function and user state must remain valid until they return.
A faulted stage turn is dropped; a fault in either callback kind removes its subscription.

### GameStart entries

The stage supports `items`/`recipes` add and `items`/`recipes`/`loot` remove; `loot` add and `drops`/`balance` entry
operations are unsupported. C# uses `RegistryStage.Add` with typed `AddLike` extensions, and typed `Remove(pattern)` in
Alter/Final. Each addition clones a present Kiln `like` chain, with stable identity, inherited fields and checked overrides.
Definitions register through the existing content path; per-world registration failures refuse dependent additions too.

Removal retains game records and id lookup, never deletes saves or already-owned copies. Removed items remain usable in
inventory, equipped slots and containers. Recipe removal hides station/all-recipes views; loot removal filters random-loot
candidates without changing direct drops. Item removal also hides dependent recipes and disables affected physical drop
slots (`chance=0`, `weight=0`, no resize). Unproven required drop/recipe rows, unsafe all-zero groups or failed visibility
bindings refuse the item removal whole, not just one exclusion path. Later registry chance/weight conflicts cannot reactivate
removed-item slots. Current build records keep drop rows unproven, so item removal is explicitly refused on those builds.

GameStart intent is retained until restart, even across code-owner retirement and changed/deleted pack entry operations.
Kiln pins each mod/id/registry's GUID and terminal game-base GUID in `kiln/data/registry/entry-compat.json`, outside per-build
caches and saves. Incompatible id reuse, corrupt records or failed atomic storage refuse the affected definition. MissingContent
persists flattened effective item definitions and restores the same identity without missing clone ancestors, recipes or
distribution. Returning mods adopt that identity; restarting without a removing mod restores original game visibility.
The [entry guide](../modding/registries/README.md#entry-add-and-remove-gamestart-only) documents the exact refusal and lifetime rules.

## Native live changes: kiln.registry.live v1

Since mod API **0.2.17**, this is a separate 56-byte table, not an extension of the stage table. Its header is
`native/core/abi/services/kiln_registry_live.h`. It is supplied by kiln-content.dll on server and client.

1. `begin(owner, entry_point, &handle)` creates an owner-bound draft.
2. `submit(owner, handle, ir, ...)` copies ordinary registry IR into it. A batch must name that owner's bound mod.
   Parsing/mapping refusals are returned per edit; known live edits remain in the draft. This is preparation only:
   use its accepted/refused counts and report, not its outcome tag as an application verdict. `commit` supplies that verdict.
3. `commit` composes the accepted edits after the preceding registry composition. It retains the handle and its IR,
   so later pack changes re-evaluate relative edits over the new pack values.
4. `revert` removes only that handle's edits and recomposes the other changes. Reverting an earlier change never puts
   an old snapshot over a later change, an older pack or a `Content.*` code owner. Revert consumes the handle.

Later commits win, relative operations compose, and `exclusive`/`yield` retain their normal meaning across entry
points. Pass names do not reorder separate transactions: A commits a Final set to 70, then B commits an Alter set to 80;
the result is 80, and reverting B restores 70. The copied base is prepared once and later turns use touched-entry overlays,
not a full-base reconstruction for each retained handle. Registry layers remain above old packs and below `Content.*` code. `read` returns actual live values through
the mapping, including lists and formulas, without creating a layer. Restart reads use the kept start-time composition;
pre-world reads include composed pending process changes. Kiln.Host's automatic runtime passes read through the
Kiln-internal `kiln.internal.registry` v2 `read_pass`: the same live value without the reading mod's own registry rows
(its stage edits and live changes), so a pass reads the pre-pass values and read-derived assignments never stack. Kiln.Host
names the mods whose code uses the registry API in the stage order, and the stage then decodes the full stable model at
resources-ready, before kiln.content writes packs into resident resource pages. A warm start with no registry user does not
decode the archive (the per-build id ledger is still written when missing); a native plugin's first begin/read/selector
then prepares the model once, outside kiln.content's store lock, from the game's bytes with Kiln's own store writes taken
back out (another loader's writes stay). Ready/status/schema inspection alone does not. Full models are cached and validated against resource bytes,
type listings, decoder/Kiln versions and the regmap. Legacy boot writers' affected resource bytes are frozen before their
writes, and their copied documents are evaluated once during deferred preparation, preserving both relative values and
writer attribution. First-use decoding/preparation can cost more than a later commit; use ModLoad when that work should
happen before a world. An unmapped or unproven field is refused, never inferred from a similar game field.

### Entry points and worlds

| Entry point | Inputs and lifetime |
|---|---|
| Game start | The existing stage table, add/alter/final. Restart fields may be baked here. No per-world inputs. |
| Mod load | Global/process inputs. A committed change made without a world waits for the first world; it follows its owner across worlds. |
| World load | A world must exist. World settings are allowed. The change expires when that world ends. |
| Hot reload | Reloaded mod/config inputs. World-scoped when committed with a world; otherwise waits for a world without per-world inputs. Kiln.Host commits a pass that replaces a mod's ModLoad point with Mod load lifetime unless it read per-world settings. |
| Manual | An explicit runtime transaction. A world must exist; it expires with that world. |

The world token is Kiln-owned and process-local, never a game address. A draft bound to one world cannot commit into
another. Native retirement only marks an owner dead; live layers are removed on the world thread at its next registry
publication. No game calls run on the owner-retiring notification thread.

A later entry point refuses each restart-only field with `RestartRequired`; it does not silently alter boot data or
quietly discard unrelated mapped live edits. Entry add/remove are GameStart-only; later requests report `RestartRequired`.
Automatic later passes keep unrelated valid field edits, but a Manual draft containing an entry-operation refusal commits
none of its valid fields. Raw game paths remain refused by this stable live API; the existing Advanced `Content.*` and raw
paths keep their previous behavior.

### Results, safety, reports and bounds

`KilnRegistryLiveResult` is 232 bytes. Its outcome distinguishes applied, waiting for a world/resource, refused,
restart-required, world-required, reverted, skipped by another writer, and unverified rollback. Accepted/refused/skipped
counts and a reason accompany it. A native argument/owner/thread/handle error uses `KilnResult`; a valid request that
cannot take effect uses the result outcome. Mod load, world load and hot reload changes refuse **per entry**, like the
stage: a selected entry that cannot take an edit (an exclusive conflict, an entry missing in this world, a config it
lacks, an inexact whole-number value) is refused alone and counted, the rest applies, and the result is Applied with
`refused > 0` and the first reason; an entry refused at binding is left out for that world only. A **Manual** transaction
has two refusal boundaries: any entry-operation refusal commits none of its valid fields, and a binding or evaluation
refusal of any field takes the whole candidate back. Field-only parse/mapping/composition-conflict/restart-field refusals
remain per edit: valid live fields in that draft may still commit. A candidate's own failed write takes it back too;
an unproved take-back is **Unverified**, not success. A failed write is whole-change at an entry point as well: per entry
refusals are for binding and evaluation, which happen before anything is written.

Cost storage is checked per entry on the running build. A fractional mana/stamina cost for an entry with whole-number
storage is refused, never rounded; float-config entries keep fractional values. A Manual transaction's result uses
`Refused`; an entry point's result is Applied with that entry counted in `refused`. Either way the reason
begins with the stable token **`not_exact: `**, followed by `items.<field> of <item> is a whole number on this build;
<value> was not written`. Tools may match that token together with the refused outcome, rather than parsing human text;
it is distinct from Unproven mapping refusals and transport errors. The native header exposes
`KILN_REGISTRY_LIVE_REASON_NOT_EXACT` (`not_exact`). The token is also kept on start-time/world-load refusal rows.

Both writes and reverts compare with Kiln's last applied bytes. If another writer has changed a slot, Kiln leaves that
slot to it for the rest of that world and names the skipped operation in the report; the change's other fields stay
applied. A retained change that cannot apply at a later application (a binding or evaluation failure, or a failed write of
one of its own slots) is inactive for that world only: its handle stays valid, a Mod load change is tried again in the next
world, and the report names its mod. A failed write is charged to the change that owns the slot, never to another change,
an unrelated pack edit or a committing candidate's own failure. The current wording is “another
writer”; naming a ShroudForge runtime package needs the later owner-write journal integration. Since 0.2.0-dev.31.17 the
field's row in `registry.json` also gets a best-effort `drift` writer naming the suspects Kiln can see (the drift probe,
[registries guide](../modding/registries/README.md#who-else-wrote-a-field)). This is not a promise to identify arbitrary
DLL writes. Since 0.2.0-dev.31.18: writes made after the last report (a later code change, another loader's write) reach
`registry.json` without a reload, at a look about 2 s after Kiln's content store changes and every 10 s otherwise (a look
reads at most 1,024 of the slots the registry writes a tick; no new generation or ready event).

Transactions queue a rewrite of `kiln/state/registry.json` and advance `kiln.registry`'s snapshot generation at the next
registry pump, outside the store lock; multiple updates before that pump may coalesce. They do not send `WORLDLOAD`
or dispatch ready callbacks from `commit`/`revert`. Only real world/pack applications send that event. A result's
`generation` is the last published snapshot, not a promise that its queued report has already been written. Nested
`commit`/`revert` calls while a transaction is in flight return `UNAVAILABLE`. Writer sources identify the live handle
and entry point. Waiting drafts do not pretend that game bytes changed.

There are at most **64 open drafts** and **1,024 retained changes** in the process, **16 open drafts** and **256 retained
changes** per owner (begin returns `UNAVAILABLE`; kiln.log names the cap), **4,096 edits per change**, **64 MiB of copied IR** in
all changes, and the stage's existing 16 MiB/32-level document limit. Revert releases a handle. Handles and world tokens
are never reused in a process. `begin`, `submit` and `status` accept any thread; live `commit`, `revert` and `read` require
the active world's thread. Without a world they operate only on copied data.

The TestKit currently marks this native table unavailable rather than pretending to simulate game byte ownership.
The native host suites exercise it over the real store, mapped readers and synthetic game memory. Real-client and
multiplayer behavior is not verified by those suites or a dedicated-server lab; a successful DLL build alone does not prove it.
