# Boot Gallery (Mod.OnBoot sample)

A status, an effect, an item, a spell and a game-tree talent that the **first** start of a server already has: `OnBoot` stages them
before the game is entered, Kiln prepares them with every mod's files and publishes them as one boot generation. `OnLoad` defines
nothing. The manifest says `"boot": true`.

**It needs the boot game-data snapshot.** `OnBoot` reads five records of the game's data (`boot.GameData.Read`): wood logs, a clay
vase, the fisher trader's template, a skill node and the StoneRough material, and the token's stack comes from the wood logs. Those
reads answer from the snapshot of the game's files that the launcher's Play step (or `kiln boot-snapshot`) makes for the installed
game build. Without one, `mods.boot.game_data` is off, the first read throws, and this mod is left out of the boot generation by
name (its runtime instance is refused until a restart); other mods are not affected.

Not baked (`KilnBake=false`): a baked file must not hide what the boot generation does.

NPCs, building materials and props are boot kinds of the chapter but not of this build: staging one answers `NotInBootKinds`, so this
sample stages the five kinds that lower today. See docs/kiln/modding/onboot.md.
