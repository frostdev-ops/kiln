# Wiki note: other mod engines (detect)

What changed for players:

- **Kiln no longer mistakes EMF 0.4 and later for ShroudForge.** Installing Kiln over EMF 0.5 chains EMF's loader,
  and DLLs that EMF's installer renamed (`dinput8-chain.ini` `[EMF]`) keep loading.
- **"Switch from ShroudForge" moves only ShroudForge's `dinput8.dll`.** `dbghelp.dll` and `winmm.dll` stay where they
  are. They may be ShroudForge's, EML's or Shroudtopia's.
- **Other loaders keep working.** Kiln takes only `dinput8.dll`. Whatever held it before (Flame, ModLoader Proxy, a
  stand-alone mod) is chained, and Ultimate ASI Loader is chained as `xinput9_1_0.dll`. Loaders under other names
  (EML, SET, Greeneye, Shroudtopia, Live Map, Shared XP) are left alone.
- **The Library says what a file is.** Mods for EML, ShroudForge, SET, Enshwick, Greeneye, Shroudtopia and Flame
  (Creative Mode), saves, Cheat Engine tables, ReShade presets, `.kfc` files and `.asi` plugins are recognised. When
  Kiln can't import one yet, it says which engine it is for and that no Library importer is registered. These files are no longer
  imported as plain DLLs that never start.
- **EML packages keep their identity.** `capabilities` is shared by EML and ShroudForge. `mod.json` plus `src/mod.lua`
  is EML unless the package has SF-specific metadata; a legacy extension with `launcher: "EML"` stays EML.

Suggested guide text (FAQ, "I dropped a mod and it was skipped"):
> Kiln tells you what the file is made for. "Kiln can't import Greeneye Loader mods into the Library yet; no importer
> is registered" describes Library support. Keep the mod in its engine's game-folder layout and use that engine's
> loader, or a compatible Kiln runtime plugin. Kiln chains loaders displaced from dinput8.dll and preserves the others.
