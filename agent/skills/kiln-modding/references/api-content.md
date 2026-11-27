# The mod API: content, creatures, abilities and assets

Part of the mod API guides ([api.md](api.md) is the index). Here: content registries and game data, creatures, abilities
(spells, buffs, Impact natives) and assets. Signatures are the common ones; the XML docs and the generated API reference
([docs-map.md](docs-map.md)) have the rest.

## Content and game data
```csharp
Content.OnReady(_ =>                              // after the packs, at every world load and pack reload
{
    Content.Items.Get("Material_T1_Wood_Logs")!.MaxStack = 2000;
    Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!.CraftingDuration = TimeSpan.FromSeconds(5);
    var r = Content.Apply("""{ "items": { "Material_*": { "maxStackSize": { "mul": 2 } } } }""");
});
Content.Items.Register(new ItemDefinition { Guid = ..., Name = "MyMod_FrostEdge", BaseOn = "Weapon_T1_1H_SwordEpic_01",
    DisplayName = "Frost Edge", Fields = new() { ["damageSetup.dmgMod"] = 1.5, ["rarity"] = "Rare" } });   // in OnLoad
Content.Recipes.Register(new RecipeDefinition { Guid = ..., Name = "MyMod_FrostEdge_Recipe", Station = "hand",
    Inputs = [("Material_T1_Twigs", 2)], Outputs = [("MyMod_FrostEdge", 1)] });
```
dev.30+ **gear:** an item type's damage, armour, levels and the rarity new copies get are `Content.Gear(item)` and the pack's
`gear` section, not raw `Fields` paths ([api-gear.md](api-gear.md#gear-values-of-a-type-contentgear-and-the-gear-section-dev30));
the rarity of an item that already exists is `Items.SetRarity` ([api-gear.md](api-gear.md)). On a new item with an item
entity, `rarity` and `itemLevelRange.minLevel` are what it is, and a copy has rarity + 1 perk slots: fill them
(`Content.Items.Get(item).SetPerks(...)`) or the item comes out one rarity lower. `docs/kiln/services/content.md` "Rarity,
level and perks of new gear".
dev.30.3+ **a recipe's tank water:** `RecipeDefinition.Water` (new recipes) and `entry.CraftingWater` (game recipes) are
`waterCharges`, charges per craft (1 charge = 64 water units), 0..1023: the six tank stations (Seedbed, Oven, Cooking
Station, Laboratory, Coffee Brewer, Beehive) take it from their own tank, crafting menus (hand, Workbench, Kitchen, NPCs)
from the player and the base's wells and barrels. Not the old "Water" canteen item. Replaces `Fields["waterCharges"]` (`Water` sends a pack's `"water"`; never both).
**Kiln registries (preferred over raw `Content` paths for data edits; experimental `KILNX031`; the full guide is
[api-registry.md](api-registry.md)):** `Mod.OnRegistry` +
`Kiln.Registry.Registry.For(context)`: `Items`, `Recipes`, `Loot`, `Drops`, `Balance`, and since 0.2.0-dev.32
`LootRules` (`game:loot`: `RarityWeights.Common..Legendary`, the world-wide rarity weights) and `RandomLoot` (every random
loot table: `Level`, `LevelOffset`, `LevelOffsetRange`, `Rarity.<rarity>.Mode/Weight`; `game:labels_*` enemies and
generic containers, `game:world_*` chests placed in the world). Edits read below the mod's own layers; for settings- or
state-driven edits declare `[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]` and call
`Registry.Reauthor(RegistryEntryPoint.WorldLoad)` when an input changes, never from inside `OnRegistry` (the pass runs
again, replacing its layers, never stacking; its layers are then the newest, over other mods' WorldLoad edits; the
re-authored pass runs on a worker thread and lands in one turn, so read settings and game state before asking and never call
tick-thread APIs in it; requests while one runs coalesce into one more pass). One call
takes at most `RegistrySchema.EditsPerCall[registry]` edits of a registry (randomLoot 16,384, the others 4,096); a call over
it is refused whole (kiln.log, registry.json), never thrown. World table ids name the table's first chest, not its index.
Check `Capabilities.Is("registry.<registry>.<field>")` per field. Page `docs/kiln/modding/registries/README.md`;
worked mod: Loot Rarity Leveler (Kiln).
Registries: `Content.Items`, `Recipes`, `Loot`, `Drops`; game data: `Content.Templates`, `Arsenals`, `Sequences`,
`Buffs`, `Perks`, `Skills`. `entry.Get(path)`, `Original(path)`, `Set(path, value)`, `Reset(path)`.
`Content.Dump()` writes every registry with its fields to `kiln/data/content/dump.json`: look names and paths up
there instead of guessing. `Content.Status()`, `Content.PackReport()`.
dev.29.18+ **the game's own texts:** on a game client, `Content.Text(locaTag)` (a string tag, a GUID or a loca id)
gives the text the game shows now, in the player's language; `Content.TextAvailable`, `Content.TextLanguage`.
Main thread of the game client only; null on a dedicated server (there `Texts.Get` stays, English, from a catalog).
`Content.TooltipValues(itemId)` gives an item type's tooltip rows (`TooltipValue`: `Label`, `Value`, `Display`), and
`GameDataEntry.TooltipValues` does the same for a perk, a buff or a skill node. Not yet checked in a real game. Page
`docs/kiln/services/content.md` "The game's own texts".
dev.29+ **buff clones:** room for 4,096 for all mods together (1,024 before); `Content.Buffs.GetBudget()` says what is
left. `Content.Buffs.Define(clone, new DefinitionRegistrationOptions(...))` answers Registered, AcceptedNextStart or
Refused with a reason instead of throwing; `GetRegistration(id)` reads it later. Define many buffs in a data pack or
before the world loads.
dev.21+: `Overrides.Claim`/`Release`/`RestoreAll` from a thread where no mod's code runs (`Task.Run`, a timer) throw
`InvalidOperationException`; post to the tick thread with `Context.Scheduler.Post`. A `ReflectedResources` handle got in
`OnLoad` stays your claim from any thread. `Assets.Reflect(key)` returns `null` on a build without kiln.types.

## Creatures
`Creatures.Template(name)`, `Creatures.Of(entity)` → `CreatureRef` (`Level`, `Faction`, `Scale`, `Resistance[kind]`,
`SpeedFactor`, `DamageFactor`, `XpFactor`, `Loot`, `Features`, `Ai.Set(path, v)`, `Reset()`), `Command.Guard /
Attack / Stay / MoveTo / Heal`. Variants: `CreatureVariants.Register(new CreatureVariant { Id, Base, Level, ... })`,
`await Creatures.Spawn(variantOrTemplate, pos)`, `CreatureVariants.AddSpawnRule(...)`. Setters are layers of your mod.
dev.26+: `Arsenals.RulesState` (`ArsenalRulesStatus`) replaces the obsolete `Arsenals.Status` (`RulesStatus`).

## Abilities
`Abilities.Register(new AbilityDefinition { Id = "mymod:name", DefaultKey = "G", ChatCommand, Cost, Cooldown,
CastTime, Targeting, OnCast = async cast => { await cast.Impact(template, pos, configs); } })`,
`Abilities.Cast(actor, id)`, `ImpactStartingEvent` (change configs or cancel before a game impact runs; narrow with
`Abilities.HoldPrograms(ids)`), `await Abilities.RunImpact(...)`, `Abilities.PlaySequence(actor, guid)`,
`await Abilities.ApplyBuff(target, buffGuid, duration, caster)`, `Abilities.Attributes.Get/Add`,
`Abilities.Natives.Register(name, argc, call => ...)` for Impact programs.
dev.27+: `await Abilities.RemoveBuff(target, buffGuid | GameBuffs.Ids.X)` → `BuffRemoveResult` (the game's own removeBuff;
`Ok` false with a reason when the entity did not have it), then `BuffRemovedEvent`. `Abilities.BuffsOf(entity)` →
`EntityBuff` (`Id`, `Slot`, `Remaining`, `Level`); the game keeps buffs on the server, so a joined client gets an empty
list. Capabilities `abilities.buffs` (pending while no server world runs), `abilities.buff_list`.
dev.26+ (not seen by a player): a mod's own **player animation states** come from `assets/player-states.json`
(`{"format": 1, "states": {"my-slash": {"clip": "my_slash", "like": "Attack_Sword_Shield_01"}}}`), merged into the
player's graph at boot, never hot-reloaded; the mod needs `"side": "both"`, `"clients": "mod"` and its cook on the server.
Sequences enter them: a weapon's `Moves` or a cast's `Animation` names them ([blocks.md](blocks.md)). `Kiln.PlayerAnim`'s
`PlayerAnimStates` (any thread) only reads: `Available`, `Graph`, `GraphHash`, `Find(mod, name)`, `FindGameStates(name)`,
`All`, `GuidOf(mod, name)`, `IdOf(guid)`; `Define(state)` is refused in this release. The player graph's key cannot be
overridden (`Assets.Override`/`Register` throw). Page: `docs/kiln/modding/animations.md` "Player states". A Kiln impact has no used item: give it
its own `Damage1` config or it hits for 0.

**Impact natives in the same step (dev.29+; server only; not confirmed in a real game).** `Abilities.Natives.RegisterSync(name, argc, handler)`
answers inside the program's call (the older `Register` suspends the program for one step). `Abilities.Natives.Attach(program, native,
stage, handler)` runs Before, Instead or After one vanilla native's calls in one program, without cloning it. Put
`[ImpactNative]` on a static method with typed parameters and the build makes the registration, the typed
`impact/natives.json` entry and a `Natives.<Name>` reference (KILN170 to KILN174 for shapes it cannot make; needs
`Abilities` in `KilnServices`). `EffectBlock.Run` (JSON `"run"`) names such a native: the effect's program calls it
first, with parameters bound by name (`self`, `target`, `attacker`, `caster`, `position`, `attackMod`, `damage`), so an
extra hit can change its own `attackMod` (KL340 for a parameter that binds to nothing). Inside a native,
`NativeContext.TryRead<T>` / `TryWrite<T>` reach components the program declares, on any entity; `NativeContext.Post`
defers to the tick. All mods share 19 free native registry entries. Handlers must not block. Sample `FrostMark`; page
`docs/kiln/modding/impact.md`; test kit `kiln.ImpactNatives`.

## Assets
`assets/assets.json` entries `icon`, `texture`, `resource`, `blob`; loaded at boot. Icons and textures are client
only. Models and sounds: sources + `assets/cook.json`, cooked on the player's machine with `kiln cook`.
dev.27+: the launcher cooks a mod with a recipe on Play, and each cook records what it wrote
(`<game>/kiln/state/cook-receipts`). A cooked file no receipt lists (shipped in a mod, copied in) loads with a warning
(`"cooked": "warn"` in `kiln/kiln.json`, the default); `"local"` refuses it, `"any"` asks nothing (development). A mod's
assets are read from its own folder only. Page: `docs/kiln/modding/cooking.md`.
dev.28+: `Assets.OpenBlobCatalog` lists every content blob the game serves and whose bytes. dev.29.16+:
`Assets.ReadResidentBlob(hash)` reads a resident blob from any thread (null until it is resident).
