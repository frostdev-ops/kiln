# The mod API, by area

Namespace `Kiln` for everything. Each service is its own assembly: list it in `KilnServices` ([setup.md](setup.md)), and
[which assembly holds a type](api-contract.md#which-assembly-holds-a-type-kilnservices) says which entry a type needs.
The guides below keep the how-to prose, rules and examples with the common signatures. **Every** public type and member, with
the mod API that added it and the first sentence of its doc, is in [api-index/](api-index/INDEX.md): generated each release, one
file per assembly named by its `KilnServices` entry, and `native.md` for the native ABI rows. Grep it
(`grep -rn "BlockAt" api-index/`) before you write a signature; the XML docs and the generated API reference
([docs-map.md](docs-map.md)) have the long text. Talents, quests, NPCs and sounds:
[quests-talents-sounds.md](quests-talents-sounds.md). Queries, terrain, refusable events, watches, stations and gems:
[gameplay.md](gameplay.md). Kiln tasks: [tasks.md](tasks.md). OnBoot and resource patches: [boot.md](boot.md).

| You want | Read |
|---|---|
| Which thread runs my code, the error rule, capabilities, `EntityRef` and `PlayerRef`, the `Mod` class, lifecycle events, other mods, **which `KilnServices` entry a type needs**, `Is.*` and `Act.*` | [api-contract.md](api-contract.md) |
| Events and filters, configs, commands and permissions, chat, players, progression, kick and bans, NoClip and player rules | [api-events.md](api-events.md) |
| Give, take, move and equip items, data kept on one item (`InstanceData`), affixes, a recipe's menu tab, knowledge tracking, ids of another loader, crafting from linked chests (`Crafting.AddSource`), **exact costs with receipts** (`ItemCosts`, dev.32) | [api-items.md](api-items.md) |
| **Gear** (dev.30+): make an item Rare, enhancements and level, one item's power and armour, rarity upgrades and upgrade stations, rarity rolls, a type's damage and armour (`Content.Gear`, the `gear` section), gear rules on the registry (dev.32) | [api-gear.md](api-gear.md) |
| Content registries and game data (items, recipes, buffs), creatures and variants, abilities and Impact natives, assets | [api-content.md](api-content.md) |
| **Registries** (dev.32): the 17 registries (`items` to `randomLoot`), a data pack's `registry` section, `OnRegistry`, `Registries.Change`, **`Registries.Reauthor`**, the edit cap per call, `RegistryViews`, what a joined client sees | [api-registry.md](api-registry.md) |
| **Pets** (dev.32): `PetBlock`, `Pets.Grant/Summon/Command/Revive`, going down, revive costs, PvP by your rule, the owner's bar | [api-pets.md](api-pets.md) |
| **Taming** (dev.32): `TamingBlock`, `Taming.Begin/Advance`, steps with exact costs, refunds, the game's own taming | [api-taming.md](api-taming.md) |
| **NPC speech and dialogue**: bubbles (`Npcs.Say`, dev.31.16+); dev.32+: greeting picks, farewells, the menu's line, talk pages (`Npcs.Topics`) | [api-npcs.md](api-npcs.md) |
| Spawn, time, settings, combat, typed ECS reads, tracked world writes, terrain edits (overlays; baked tiles are tools only in dev.32), **block queries and breaking** (`Build.BlockAt`, `FindWall`, `BreakBox`), **building tools** | [api-world.md](api-world.md) |
| Menus, screens, HUD, key bindings (also controller keys), themes, item icons, values in the game's UI, the game's inventory slots, tooltip lines, outlines | [api-ui.md](api-ui.md) |
| Network channels, Steam messages, the client (`BuildingInput`, `SteamGuard`) | [api-network.md](api-network.md) |
| Hooks (advanced tier), another mod's DLL, native plugin rows, typed hooks, EML, EMF, other engines' mods | [api-hooks.md](api-hooks.md) |

Where the sections of the old single page went (links to `api.md#...` land here):

| Old section | Now |
|---|---|
| Threads, in one table; The API contract; The mod; Lifecycle events; Other mods | [api-contract.md](api-contract.md) |
| Events; Configs; Commands and permissions; Chat; Players and progression; Player rules and NoClip | [api-events.md](api-events.md) |
| Items; Inventory transfers; [Persistent data](api-items.md#persistent-data) (`InstanceData`, affixes); [exact costs](api-items.md#exact-costs-and-receipts-dev32) | [api-items.md](api-items.md) |
| Items, spawn, world, time, settings, combat | [Items](api-items.md#items); [Spawn, world, time, settings, combat](api-world.md#spawn-world-time-settings-combat) |
| Content and game data; Creatures; [Abilities](api-content.md#abilities); Assets | [api-content.md](api-content.md) |
| Typed ECS reads; Tracked world writes; World edits; ECS world programs | [api-world.md](api-world.md) |
| Game UI, HUD, keys | [api-ui.md](api-ui.md) |
| Network; [Client](api-network.md#client) | [api-network.md](api-network.md) |
| Hooks and other DLL mods; Other engines' mods next to yours | [api-hooks.md](api-hooks.md) |
