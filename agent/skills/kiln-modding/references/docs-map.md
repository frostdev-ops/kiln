# Where the answers are

The wiki: https://frostdev.io/kiln/wiki/<page>.

| Question | Page |
|---|---|
| Start a project | `first-mod`, `dev-kit` |
| Make a spell, a status, an effect, an item or a weapon | `blocks`, `new-items` |
| Make a prop, a station that works on its own (a forge, a drying rack), comfort | `world-blocks` |
| A cosmetic, a hat, a cape: a model worn on the body | `blocks`, `models` |
| The `kiln` command | `cli` |
| Manifest, sides, `clients`, version ranges | `manifest` |
| Events, configs, commands, saved data, other mods | `code-mods` |
| Hot reload, the control folder | `hot-reload` |
| In-server tests | `testing` |
| Why a mod didn't load, faults, breakpoints | `debugging` |
| Linter and analyzers | `lint` |
| VS Code | `vscode` |
| Game ids | `catalogs` |
| Items, recipes, loot, drops | `data-packs` |
| New items | `new-items` |
| Move gear with its state; quick-stack rules; chest names | `inventory` |
| Boot ECS declarations, worker callbacks, other-entity writes | `world-programs` |
| Spells, creatures, attacks as data; clones | `game-data` |
| One creature, variants, spawn rules, guard | `creatures` |
| Scripted abilities, per-cast tweaks, buffs | `abilities` |
| Impact programs | `impact` |
| Levels, XP, skill points | `progression` |
| New skill nodes, talent trees | `talents` |
| Quests, conversations | `quests` |
| Placed and new NPCs, the game's journal and menus | `npcs` |
| Sounds, music, voice | `sounds` |
| Particles, lights, the game's VFX: [vfx.md](vfx.md) | `blocks`, `shaders` |
| Icons, retextures, resources | `assets` |
| 3D models | `models` |
| New surfaces for models from PNGs and numbers | `blocks`, `shaders` |
| Terrain, props, schematics, zones | `world-edits` |
| Menus, screens, crafting tabs, HUD, keys | `game-ui`; data blocks: `ui-blocks` |
| Network channels, synced configs, client code | `multiplayer` |
| Mods of other engines (EML, EMF, closed DLL mods), chain loading | `other-mods` |
| Forge concepts | `forge` |
| What works today | `status` |

dev.25+ (in development; pages in the repository's `docs/kiln/`, not on the wiki yet):

| Question | Page |
|---|---|
| What a mod cannot do yet, hard caps, the planned release | `modding/limits.md` |
| Error rule, `EntityRef`/`PlayerRef`, side and thread markers | `modding/contract.md` |
| What works on this build and why not (`Capabilities`, `capabilities.json`) | `services/caps.md` |
| Typed ECS reads (`Kiln.Ecs`), layout stamps | `typegen.md` §4, §6 |
| Unit tests with no game (`Kiln.TestKit`) | `modding/unit-tests.md` |
| Guided client checks (`[ClientCheck]`) | `modding/testing.md`; the pack: `client-pass.md` |
| The achievement guard (`Kiln.SteamGuard`) | `services/steamguard.md` |

Sample mods: Hello, SampleLib, SampleConsumer, SampleTests, BigStacks, NewItems, SpellTweaks, CreatureTweaks, EliteVukah, FireNova,
ImpactSample, AssetSample, ModelSample, SoundSample, VfxSample, SkillTreeSample, quests-sample, NpcSample, Schematics, MenuButtons, UiSample,
NetSample, Claims. dev.20+: UiBlocks, WorldPrograms and the block templates in [setup.md](setup.md).

Kiln's reverse-engineering notes are for its own developers. Mods never need them. Don't copy addresses or offsets into mod code.
