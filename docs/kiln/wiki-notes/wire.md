# Wiki note (agent wire): every engine's mods in the launcher

What changed for the public guide (frostdev.io/kiln/wiki). Suggested text below; the technical reference is
`docs/kiln/engines.md` ("Managing every engine's mods") and `docs/kiln/launcher/contracts.md` 5.1 and 5.8.

## Suggested section: "Mods for other loaders"

> Kiln runs mods made for other Enshrouded mod loaders, and the launcher manages them like Kiln mods. Drop a mod
> made for **EML**, **ShroudForge**, **Shrouded Engine Tweaks**, **Greeneye Loader**, **Creative Mode (Flame)** or
> **Shroudtopia** on the launcher window, or import it on the Library page. The Library shows which loader each mod
> was made for. Turn mods on and off per profile, like Kiln mods. You don't need the other loader installed: Kiln's
> own plugin for that loader runs the mod.
>
> - **ShroudForge** and **Shrouded Engine Tweaks** mods are copied into the folder those loaders use
>   (`mods/` and `shrouded_engine_tweaks/mods/`) when you activate the profile. Folders you put there yourself are
>   never touched.
> - **Settings:** ShroudForge mods get a settings page built from their own settings. Shroudtopia mods' settings are
>   kept per profile. EMF mods keep their ini editor.
> - **EML** changes the game's data files. To remove EML changes, disable EML mods and launch through updated
>   Kiln. Its archive writer restores only proven, unchanged EML output; another loader's data is kept. A difference
>   from a shared backup alone never authorizes restoration.
> - **Servers and modpacks:** the server's Mods page puts each mod where its loader looks on the server. A server
>   modpack includes the EML, ShroudForge and Creative Mode mods that players need, so joining players get them
>   in one step.
> - Mods for **Enshwick**, **shroudlink** and **Ember** are recognised, but Kiln can't import them yet.

## Notes for the guide agent
- Mods for other engines are imported as plain copies. Kiln never runs the other loader's own DLL.
- A ShroudForge mod that a profile no longer lists is switched off, not deleted, so its settings stay.
- On a server, a switched-off mod for another engine moves to `kiln/disabled/<kind>/`.
