# Tiers: what survives a game update

Every public surface of Kiln is in one of three tiers. The tier says what happens to a mod that uses it when Enshrouded
updates: whether Kiln keeps it working, checks it, or leaves it to the mod. The tiers are the owner's decision D5
(2026-11-20, `dev31/DECISIONS.md`), from the registry design (BRIEF section 7).

| Tier | What it names | After a game update | API 1.0.0 |
|---|---|---|---|
| **Stable** | Kiln's own names only: registries, Kiln ids and fields, typed blocks, Kiln's services by Kiln name | Kiln updates its mapping; the mod keeps working unchanged. A field Kiln has not proven on the new build yet is refused per entry with a reason until a Kiln update proves it, never guessed | the contract is frozen: names, ids, fields, their types, ranges and when they apply change only additively |
| **Advanced** | the game's own names: types, members and reflection paths, debug names, resource GUIDs | works while the game's names and layouts hold; Kiln checks each path against the running build's reflection and refuses an entry it cannot resolve, loudly, never with silent corruption | stays, with no removal date; outside the update promise |
| **Native** | addresses, raw memory, native code | the mod's own risk on every update | stays, with no removal date; outside the update promise |

What **API 1.0.0 freezes**: the Stable tier's contract, meaning every registry schema at 1.0.0 (`schema/registry/*.json`:
registry and field names, the id rule, types, ranges, `applies`, `joinedClient`; additive only within the major,
[registries/README.md](registries/README.md) "Versions"), the C# that edits and reads them, the data packs' `registry` section,
typed blocks and the Kiln-named services. The C# signatures of every public API, every tier included, stay additive from
1.0.0 as the mod API's semver says (PublicAPI files); the Advanced and Native tiers are frozen in their signatures, not in
what they promise across a game update.

The hints are info level only ([analyzers.md](analyzers.md#kiln124)): they name the stable replacement where one exists
and never refuse a mod. Nothing old is removed: there is no removal date for the Advanced or Native tier.

**A naming note.** The manifest's `"advanced": true` (and `<KilnAdvanced>`, KILN107) is older than these tiers: it opts a mod
into hooks, symbols and raw memory, which is the **Native** tier here. Since 0.2.0-dev.32 the manifest also takes
`"native": true`, the same setting under the tier's name: give either (both: the same value, or Kiln.Host refuses the
manifest). `"advanced"` keeps working with no removal date. A Kiln before 0.2.0-dev.32 does not know `"native"` (it warns
about an unknown key and does not opt the mod in), so a mod for older Kilns keeps `"advanced": true`; `<KilnAdvanced>` still
writes `"advanced"`. The Advanced tier needs no opt-in.

## Every public surface

| Surface | Tier | Notes |
|---|---|---|
| Registries: `schema/registry/*.json`, Kiln ids (`game:<stable name>`, `<mod>:<name>`), Kiln fields | Stable | experimental (KILNX031) until marked stable; then frozen |
| `Kiln.Registry`: the typed entries, `RegistryStage` (and `Entries(name)`), `RegistrySchema`, `Registry.Change` / `Read` / `Ready` | Stable | [registries/README.md](registries/README.md) |
| `Mod.OnRegistry`, `[RegistryEntryPoints]`, `RegistryContext` (Kiln.Api) | Stable | |
| A data pack's `registry` section (`content/*.json`) | Stable | [registries/README.md](registries/README.md) |
| `RegistryViews` and `kiln.registry.view`: a **stable** view (a registry) | Stable | [registries/views.md](registries/views.md) |
| `RegistryViews` and `kiln.registry.view`: an **advanced** view (any other resource type) | Advanced | Kiln names derived from the game's reflection, verified per game build, read-only |
| Typed blocks: `blocks/*.json`, `Kiln.Blocks`, `BootDefinitions` | Stable | [blocks.md](blocks.md) |
| Kiln's services and events by Kiln name (Kiln.Items, Kiln.World, Kiln.Players, Kiln.Chat, ...) | Stable | Kiln maps them onto each game build (sigdb) the way it maps registries; a member that takes the game's names is Advanced (below) |
| `raw` member paths in a registry edit | Advanced | checked per entry against the build's reflection |
| Data pack sections with the game's paths: `items`, `recipes`, `loot`, `drops` (kiln.content); `templates`, `arsenals`, `sequences`, `buffs`, `perks`, `skills`, `itemdata` (kiln.gamedata) | Advanced | KILN124 names the registry field that covers a path |
| `ContentEntry` / `GameDataEntry` paths (`Content.Items.Get(x).Set("maxStackSize", ...)`), `Content.Apply` JSON | Advanced | KILN124 on a constant path a registry field covers |
| Resource patches: `resourcePatches` in blocks files, `ResourcePatchBlock` | Advanced | [resource-patches.md](resource-patches.md); KILN124 on covered paths |
| `boot.GameData` keys (`BootContext.GameData`) | Advanced | [boot-gamedata.md](boot-gamedata.md) |
| `GameFields`, `GameIds` (Kiln.GameIds, `kiln catalog build`) | Advanced | generated from the player's own game; [typed-fields.md](typed-fields.md) |
| `GameTypes`, `Reflected`, `GameEvents` and `GameSystems` by the game's names (kiln.types) | Advanced | [services/types.md](../services/types.md) |
| Hooks by address or by symbol (`Hooks.Detour`, `Hooks.Mid`), `Symbols`, `Constants`, a mod's own `sigdb/` and typed hooks (Kiln.Sigs) | Native | needs `"advanced": true`; KILN125 names the registry field that covers a hooked function where one does |
| Raw memory: `Memory.Read/Write/Patch`, `Marshal` and pointers into the game's memory | Native | KILN125 |
| Native plugins (`native.json`, the C tables of `native/core/abi`) | Native | the C tables are Kiln-named and additive, but a native plugin runs in the game's process with no check |
| Other engines' mods (ShroudForge, EML, EMF DLLs, dllmod) | Native | Kiln describes their effects in registry terms where it can (registries README "Who else wrote a field") |

In the API reference each type's page says its tier ("Tier: ..."), from `tools/docs-site/tiers.json` (every assembly has a
default tier; types of another tier are listed by name; a test fails when an assembly has none).

## Moving a mod up a tier

| You use | Move to | How |
|---|---|---|
| a data pack path the registry covers (`"maxStackSize": 500` on an item) | the registry field (`items.maxStack`) | KILN124's fix moves a one-to-one value into the pack's `registry` section; relative ops stay where they are, since an old section's op works on the game's value and the registry's on the value below it |
| `ContentEntry.Set("maxStackSize", ...)` in code | `Registry.Change(view => view.Items[id].MaxStack = ...)` or `OnRegistry` | by hand: a registry change is a transaction on the world thread with a result, not a code layer above every pack |
| a resource patch on the balancing table | `balance.*` fields | by hand, in the registry section or `OnRegistry` (GameStart) |
| a member no registry has yet | the advanced view (`RegistryViews.Find("quest")`) to read it under Kiln names, or the old path to write it | a view field that a registry covers says so (`CoveredBy`) |
| a hook of a game function Kiln owns a rule for (the altar limit) | a registry field (the `limits` registry's `maxAltars`, [limits.md](registries/limits.md)) | KILN125 names the field when the registry's regmap row names the function |
| a hook by address (`Hooks.Mid(base + rva)`) | a Kiln service, a registry field or an event, case by case | KILN125 marks it; until Kiln has one, the hook stays the mod's own |
