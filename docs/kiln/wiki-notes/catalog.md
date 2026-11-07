# Wiki note: the Catalog (mods from Nexus Mods in the launcher)

What changed: the Kiln Launcher has a Catalog of Enshrouded mods on Nexus Mods, with Kiln mods first. You can browse
and search, see a mod's details, and add it to your active profile with Undo. Mods added from the Catalog show when
their author uploads a newer version, and updating is one click.

The launcher can also take Nexus's **Mod Manager Download** links (opt-in, Enshrouded only; other games' links go to
your previous mod manager). It works in the desktop UI and in Deck mode.

References:
- `docs/kiln/launcher/catalog.md`: how the catalog works, Nexus's rules, the checklist.
- `docs/kiln/launcher/catalog-authors.md`: for mod authors.

## Suggested page: "Getting mods: the Catalog" (under Using the launcher, after "Adding mods")

> **The Catalog** (desktop: Catalog in the top bar; Deck mode: the Catalog tab) lists Enshrouded's mods on Nexus Mods.
>
> 1. In **Settings > Nexus Mods**, paste your personal API key (nexusmods.com, Settings, API Keys) and press Save.
>    The launcher checks the key with Nexus first. It keeps the key in your system keyring (or in its own folder,
>    readable only by you) and never shows it again.
> 2. Open the **Catalog**. **Kiln mods** shows the mods made for Kiln. **All Enshrouded** shows every mod, and marks the
>    EMF, native and DLL mods Kiln can load too. **Installed** shows what you added from here. You can search by name or
>    author, and sort.
> 3. Pick a mod to see its pictures, description, author, version, endorsements, requirements and files.
>    - **Premium accounts:** **Add** downloads the mod, checks it against Nexus's file list, and puts it in your active
>      profile. The toast has **Undo** and **Get** for the mods it needs.
>    - **Free accounts:** Nexus wants free downloads to go through its website. **Download on Nexus Mods** opens the
>      file's page, where you press **Mod Manager Download**. With step 4 turned on, the mod comes straight back into
>      the launcher.
> 4. To use **Mod Manager Download**, turn on **Settings > Nexus Mods > Mod Manager Download links**. Enshrouded links
>    then open in Kiln. Links for other games still go to the manager you had before (Vortex, Mod Organizer 2), and
>    turning the option off gives every link back to it.
> 5. **Updates:** when a mod you added has a newer file, its card says **Update** and a banner lists it. Nothing updates
>    by itself: click Update.
>
> Without a key the Catalog still lists every Enshrouded mod, but it can't tell which are Kiln mods or download them.
> The launcher on Nexus Mods (which never updates itself) has the Catalog too: it downloads only the mods you pick.

## Suggested section for the modding guide: "Showing up in the Catalog" (under Publishing your mod)

> Your mod is a **Kiln mod** in the launcher's Catalog when a file on its Nexus page has a `kiln.mod.json` in its zip,
> at the root or in one folder. `kiln pack` makes such a zip. The launcher reads this from Nexus's "Preview file
> contents" list, so no download is needed.
>
> - Upload a **zip** (7z and RAR are refused) and put the Kiln mod in your **main** file.
> - Keep the version on Nexus and the one in `kiln.mod.json` the same. Upload new versions into the **same file**
>   ("Upload a new version"), so players see them as updates.
> - Set requirements with Nexus's file requirements (add the Kiln page, mod 301, too). The launcher lists them and
>   offers the missing ones after an install. Keep `dependencies` in `kiln.mod.json` as well: Kiln checks those at load
>   time.
> - Leave **Mod Manager Download** on: free players add your mod through it.

## Notes for the guide agent
- Screenshots of the mock UI: `.out/catalog/shots/` (`desktop-catalog.png`, `desktop-details-free.png`,
  `desktop-nxm-added.png`, `desktop-settings.png`, `deck-catalog.png`, `deck-mod.png`, `deck-added.png`). The mods in
  them are made up: replace them with real ones once the Catalog has run against the real API.
- Not yet: collections, 7z/RAR, legacy page-level requirements (Nexus API v3 has only file requirements), and update
  notices for mods added from files before the Catalog existed.
