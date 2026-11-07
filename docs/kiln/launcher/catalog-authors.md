# Getting your mod into the Kiln Launcher's catalog

The Kiln Launcher has a Catalog of Enshrouded mods on Nexus Mods. Its default view, **Kiln mods**, shows the mods that
Kiln can load as Kiln mods. Players add them with one click (premium accounts) or with Nexus's **Mod Manager Download**
button (everyone else). How the launcher does it: [catalog.md](catalog.md).

## The rule
Your mod shows up as a Kiln mod when **a file on its Nexus page holds a `kiln.mod.json`**:
- The file is active: main, update, optional or miscellaneous (not archived). The launcher looks at your three most
  recently updated files and at each one's newest version.
- The `kiln.mod.json` is at the root of the zip or in one folder (`MyMod/kiln.mod.json`, the way `kiln pack` makes it).
- The page was created or updated after 2026-11-01.

The launcher reads this from the file list Nexus shows under "Preview file contents", so nothing is downloaded to
check. A changed page is looked at again.

## Checklist for authors
1. Upload a **zip** (not 7z or RAR: the launcher refuses those) with your mod folder in it, as `kiln pack` makes it.
2. Put the Kiln mod in your **main** file. Optional files (extra configs, a texture pack) can sit beside it. The
   launcher's Add takes the Kiln file, else your primary file, else your newest main file.
3. Use a version Nexus and Kiln agree on: the same version in the Nexus file's version field and in `kiln.mod.json`
   (`1.2.0`, `1.2.0-beta.1`). The launcher compares the Nexus versions to find updates. A newer upload in the **same
   file** (Nexus's "upload a new version") counts as an update; a new separate file does not.
4. Requirements:
   - Set your mod's requirements with Nexus's file requirements (the requirements on your file, which the API shows).
     The launcher lists them and offers "Get <mod>" after an install.
   - Add the Kiln page (Enshrouded mod 301) as a requirement too: players without the launcher see it, and the
     launcher hides it (it is Kiln).
   - Also keep `dependencies` in `kiln.mod.json`: that is what Kiln checks at load time.
5. Turn on **Mod Manager Download** for the file (the default). Free players add your mod through it.
6. Pictures and a short summary help: the catalog shows your primary image, gallery, summary and endorsements.

## What players see
- **Kiln mods:** your mod with "Kiln mod". Under **All Enshrouded**, other mods Kiln can load show as EMF, native or
  DLL mods.
- **Details:** your pictures, description (as text), author, version, endorsements, requirements and files.
- **Add:** the file goes into their active profile with Undo. Requirements they lack are offered.
- **Update <version>:** shown when your file has a newer upload. Players click it; nothing updates by itself.

## Native, EMF and DLL mods
These load in Kiln too:
- a `native.json` mod;
- an EMF mod (`mod.json` plus its DLL);
- a bare DLL.

They show under All Enshrouded and add the same way. Only `kiln.mod.json` mods are in the default Kiln mods view.
