# Wiki note: EML mods (agent eml, 2026-11-07)

What changed for the public guide (frostdev.io/kiln/wiki): Kiln now runs **EML mods** (Enshrouded Mod Loader, the
most common data mods on Nexus) through the bundled plugin kiln-eml. Suggested text for a new page "EML mods" (and a
line on the "Mods from other loaders" page):

> **EML mods work in Kiln.** Put them in your game's `mods` folder as the EML guide says, or add them to a profile in
> the launcher. You don't need EML's `dbghelp.dll`, and on Linux/Steam Deck you don't need the
> `WINEDLLOVERRIDES=dbghelp=...` launch option: Kiln starts EML mods itself, before the game loads its data.
>
> Kiln applies EML mods the way EML does: it patches the game's data files once and keeps a vanilla backup
> (`enshrouded.kfc.bak`). It patches again only when a mod or the game changed, so later starts are fast. When you
> remove every EML mod, Kiln puts the vanilla data back.
>
> A mod with an error is switched off and the others still apply. The launcher's log (`kiln.log`) names the mod and the
> error, and `kiln/state/eml.json` lists every EML mod with its state.
>
> If you also have EML's own `dbghelp.dll` installed (with the Wine override on Linux), EML applies the mods and Kiln
> steps aside (`kiln-eml: standing aside` in kiln.log). Both can't patch the same files.
>
> kiln-eml is licensed GPL-3.0 like EML (its licence and source are shipped with Kiln; Kiln itself stays MIT).

For mod authors (a short section on the API page): EML mods need no changes. Kiln mod authors get EML-style helpers in
C#: `DataHash.Fnv1a32/Crc32/Crc64`, `DataBuffer` (EML's buffer), and `Eml.IsLoaded("<eml mod id>")`
(docs/kiln/eml.md section 4).
