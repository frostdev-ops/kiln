# Wiki note: mods for Greeneye Loader, Flame (Creative Mode) and Shroudtopia

What changed: Kiln now loads three more kinds of mods made for other loaders, through plugins that ship with Kiln
(`kiln/plugins/kiln-greeneye.dll`, `kiln-flame.dll`, `kiln-shroudtopia.dll`). Full pages: `docs/kiln/engines/`.

Suggested text for the guide ("Mods for other loaders" page):

> **Greeneye Loader mods** (The Worst Map Mod, Find, Quick Deposit, Town Portals, Embervale Teleport, Hollow Halls
> Progression): put each mod in `greeneye/<id>/` in the game folder, as its download says, and leave out the
> `dbgcore.dll` that comes with it. Kiln runs them, also on Linux and Steam Deck, where Greeneye Loader itself does
> not start.
>
> **Creative Mode** (and other Flame mods): put its `mods/creative/` folder in the game folder and leave out its
> `dinput8.dll` (Kiln is the game's `dinput8.dll`).
> Don't switch on Kiln's own creative rules at the same time: both change building cost, flight and time of day.
>
> **Shroudtopia and Shroudweave mods** (Minimap and Weather Clock, first-person view, basics): put the mod DLL in
> `mods/` and leave out `winmm.dll` and `shroudtopia.dll`. Settings are in `kiln/config/shroudtopia/shroudtopia.json`
> (Kiln copies your old `shroudtopia.json` there the first time); switch a mod with its `"active"` value, even while
> the game runs.
>
> If the other loader is still installed and starts, Kiln leaves its mods to it and says so in `kiln/logs/kiln.log`.
> Each loader's mods are listed with their status in `kiln/state/greeneye.json`, `flame.json` and `shroudtopia.json`.

For authors of advanced mods (Kiln API, `"advanced": true` in `kiln.mod.json` only): `Kiln.Hooks` gained
`OriginalCode.FindUnique` / `Read` (find and read the game's code as it is in the exe file, even where another mod
hooked it) and `Keep.Block` (memory that survives a hot reload). They need the calling mod's context: call them in
`OnLoad` or a Kiln callback and cache what a background thread or raw job handler needs.
