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
  Kiln can't load one yet, it says which engine it is for and that support is coming. These files are no longer
  imported as plain DLLs that never start.

Suggested guide text (FAQ, "I dropped a mod and it was skipped"):
> Kiln tells you what the file is made for. "a Greeneye Loader mod. Kiln can't run Greeneye Loader mods yet; support
> is coming" means the mod needs that engine. Kiln is adding the engines one by one. Until then, keep using the
> engine's own loader: Kiln chains it and doesn't remove it.
