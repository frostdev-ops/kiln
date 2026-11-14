# Boot Gallery (Mod.OnBoot sample)

A status, an effect, an item, a spell, a game-tree talent and a resource patch that the **first** start of a server already has: `OnBoot` stages them
before the game is entered, Kiln prepares them with every mod's files and publishes them as one boot generation. `OnLoad` defines
nothing. The manifest says `"boot": true`.

**It needs the boot game-data snapshot.** `OnBoot` reads five records of the game's data (`boot.GameData.Read`): wood logs, a clay
vase, the fisher trader's template, a skill node and the StoneRough material, and the token's stack comes from the wood logs. Those
reads answer from the snapshot of the game's files that the launcher's Play step (or `kiln boot-snapshot`) makes for the installed
game build. Without one, `mods.boot.game_data` is off, the first read throws, and this mod is left out of the boot generation by
name (its runtime instance is refused until a restart); other mods are not affected.

It also stages one **resource patch** (`friction-mark`): the first terrain config's `frictionFactor` in the game's terraforming
registry (`keen::TerraformingEfficiencyRegistryResource` 15506ca1-f0b2-8a43-a087-75a7c83d5596), 1.0 in the game, 0.999 here. Nobody
can see a 0.1 % change; it is there so a start can read the patched value back (`OnLoad` logs it once the content is ready, and the
client pass's check 08-boot.first-start-definitions reads it in the game client). Do not run it together with boot-patches-test's
"shared" case, which sets the same field: two mods on one field are refused.

**Server-only variant** (the client pass's joined case, check 08-boot.first-start-definitions part 2 (A)): the same folder with
`"side": "server"` in its kiln.mod.json instead of `"both"`. Copy the folder, change only that line, and enable the copy instead of
boot-gallery before the start: the game client then has no OnBoot of its own for it, and what a joined server committed has to be
there at once.

Not baked (`KilnBake=false`): a baked file must not hide what the boot generation does.

NPCs, building materials and props are boot kinds of the chapter but not of this build: staging one answers `NotInBootKinds`, so this
sample stages the five kinds that lower today. See docs/kiln/modding/onboot.md.
