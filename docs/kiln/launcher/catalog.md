# The online catalog (Nexus Mods)

The launcher's Catalog lists Enshrouded's mods on Nexus Mods, Kiln mods first. It shows each mod's details, adds a
mod to the active profile (with Undo), takes "Mod Manager Download" (`nxm://`) links, and tells you when an installed
mod has a newer file. Desktop: the Catalog page (`/catalog`) and Settings > Nexus Mods. Deck mode: the Catalog tab.
Mod authors: [catalog-authors.md](catalog-authors.md).

Code:
- `crates/kiln-manage/src/nexus/`: the API client, the catalog, downloads, nxm parsing, the handler registration and
  the key store (unit tests in `tests.rs` against a mock Nexus Mods).
- `launcher/src-tauri/src/commands/nexus.rs`: the `nexus_*` commands, the link inbox, `kiln://nxm`.
- `launcher/src/api/nexus.ts` and `mockNexus.ts`; `src/pages/CatalogPage.tsx`; `src/components/NexusSettings.tsx`;
  `src/shell/catalogInstall.tsx` (the shared Add flow and the nxm listener); `src/deck/screens/Catalog.tsx`.
- Contracts: [contracts.md](contracts.md) sections 3 (`<data>/nexus/`), 9 (`nexus_*`, the types), 10 (`kiln://nxm`)
  and 12.

## Which mods are Kiln mods
A mod counts as a Kiln mod when one of its active files (main, update, optional, miscellaneous) has a `kiln.mod.json`
in its newest version's archive, at the root or in a folder. Kiln checks the three most recently updated files.

Kiln reads this from Nexus's archive manifest (`manifest_url`, the file list Nexus publishes for each upload), so
nothing is downloaded to find out. The same look sorts out the other mods Kiln can load:
- `native.json`: a native mod
- an EMF `mod.json` next to a DLL: an EMF mod
- a bare DLL: a DLL mod
- `kiln-modpack.json`: a modpack

**All Enshrouded** shows every mod, with these words on the ones that were looked at.

Why this rule and not the others:
- **Tags:** Nexus has no tag an author can add for Kiln, and its search can't filter by tag.
- **A requirement on the Kiln page (301):** this is a per-file setting that authors often skip. Requirements are
  shown in the details instead.
- **The manifest:** it is exactly what the launcher can import, and authors need to do nothing beyond packing the
  mod the normal way.

Cost and caching:
- Only mods created or updated after `KILN_EPOCH` (2026-11-01) are looked at. Kiln did not exist before that date.
- A look costs about three API calls (the files, their versions, and the manifest on Nexus's storage).
- At most 12 mods are looked at per search. The page asks again while `unchecked` > 0.
- The answers are kept in `<data>/nexus/verdicts.json` per mod and its `updated_at`, so a mod is looked at again only
  after its page changes.
- The mod list itself is kept 10 minutes. Enshrouded had 237 visible mods (3 search pages) on 2026-11-07.

Files and manifests need the user's API key. Without one the catalog still lists every Enshrouded mod (search and mod
pages work anonymously) and says that finding Kiln mods needs the key.

## Nexus's rules for apps, and how Kiln follows them
- **Naming the app:** every request sends `Application-Name: Kiln Launcher`, `Application-Version: <launcher version>`
  and `User-Agent: Kiln-Launcher/<version> (<os>; <arch>)`. No user name, machine name or anything personal.
- **The key:**
  - The user pastes their personal API key from nexusmods.com/settings/api-keys.
  - It is checked with Nexus (`/v1/users/validate.json`) before it is kept. Of that answer only the account name and
    the premium flag are kept, in memory.
  - It is stored in the OS keyring: the Secret Service via `secret-tool` on Linux (GNOME Keyring or KWallet),
    Credential Manager on Windows. Without a keyring it goes to `<data>/nexus/apikey.json`, mode 0600.
  - It never goes to settings.json, the UI, a log or an error message.
  - It is sent only to `api.nexusmods.com`, never to the CDN or the manifest storage.
  - `KILN_NO_KEYRING=1` skips the keyring.
- **Rate limits:**
  - One request at a time, at least 150 ms apart.
  - The `x-rl-hourly-remaining` / `x-rl-daily-remaining` headers are read. Requests stop when both drop under 20, so the
    user's other tools keep some budget.
  - A 429 blocks the client for its `Retry-After`.
  - Caches keep the use low: the list for 10 minutes, verdicts per `updated_at`, update checks 30 minutes per file.
- **Downloads:**
  - Premium accounts download through the API (`download-link` without a key).
  - Free accounts must download through the website. For them Add opens the file's page
    (`?tab=files&file_id=<id>&nmm=1`, which shows its Mod Manager Download). That button makes an nxm link carrying
    `key` + `expires`, which the launcher takes (below). The launcher never works around this.
- **The Nexus Mods build:** it downloads mods the user picks, the same as the direct build. It never downloads or
  replaces itself ([releasing.md](releasing.md), "The two builds").

## Adding a mod
1. **The file:**
   - From the catalog: the file shown in the details as "Add takes this". That is the Kiln file, else the primary
     file, else the newest main file.
   - From an nxm link: the file the link names.
2. **The download:** download links (4 hours), then the first link that works (https, at most 2 GB) into
   `<data>/nexus/downloads/<mod>-<file>/`.
3. **The check:**
   - The file must be a zip or a DLL. A 7z or RAR is refused: "download it by hand and unpack it".
   - Every file that Nexus's manifest lists with a size must have that size in the zip, and its sha256 must match when
     the manifest has one. Paths are matched as they are, then without a top folder.
   - `library::detect` must find something Kiln loads.
4. **Adding it:** `quick::add` imports it into the library and adds it to the active profile ("My mods" for a
   first-time player). It also writes the game's `profile.json`, the same as a dropped file (contracts.md section 7,
   "Add a mod").
5. **The record:** the mod, its file and version and the library entries go to `<data>/nexus/installs.json`.
6. **What it shows:**
   - The file's requirements on Nexus (its dependency ranges) come back with the report.
   - The toast says "Added <name>" with Undo (`profile_revert`; the library copy stays).
   - It also has "Get <requirement>" for each one that is not installed, not Kiln itself and for Enshrouded. That
     opens the catalog on that mod.
   - Deck mode shows the same as a dialog: OK, Undo, Get.

## Updates
- The Catalog page checks the installed mods' files once when it opens (`nexus_installed(check: true)`). Each file is
  checked at most every 30 minutes.
- A mod has an update when the newest listed version of its file is another upload and its version string is not
  older ([`nexus::version`](../../../crates/kiln-manage/src/nexus/version.rs) compares Nexus's free-text versions).
- The card says "Update 1.1.0", a banner lists the updates, and the details offer "Update to 1.1.0" (premium) or
  "Update on Nexus Mods" (free).
- Updating is that click, never automatic.

## Mod Manager Download links (nxm://)
This is opt-in: Settings > Nexus Mods > "Mod Manager Download links". It never turns on at install or start.

- **Linux:**
  - Kiln writes `$XDG_DATA_HOME/applications/kiln-launcher-nxm.desktop`
    (`Exec="<launcher>" --nxm %u`, `MimeType=x-scheme-handler/nxm;`, `NoDisplay=true`).
  - When run as an AppImage, `<launcher>` is the AppImage itself (`$APPIMAGE`), not its temporary mount.
  - Then `xdg-mime default kiln-launcher-nxm.desktop x-scheme-handler/nxm`, and `update-desktop-database` when it is
    there.
- **Windows:** `HKCU\Software\Classes\nxm` (`URL:NXM Protocol`, `URL Protocol`, `DefaultIcon`,
  `shell\open\command` = `"<launcher>" --nxm "%1"`).
- **The handler before Kiln:**
  - On Linux this is `xdg-mime query default`. On Windows it is the HKCU command, else HKLM's.
  - It is kept in `<data>/nexus/handler.json`. Settings names it ("Vortex", "Mod Organizer 2").
  - Links for other games go to it unchanged: its desktop entry's `Exec` with the link filled in, or its command with
    `%1`.
- **Turning it off:**
  - Linux: `xdg-mime default <previous>`. With no handler before Kiln, Kiln's entry is removed from mimeapps.list. The
    `.desktop` file goes.
  - Windows: the previous HKCU command and icon are written back, or Kiln's HKCU key is removed so a machine-wide
    handler shows again.
- **Another program:** if one takes the links back later, Settings says so ("turn this off and on again").
- **One launcher:** the browser starts a second launcher process for each link. That process writes the link to
  `<data>/nexus/inbox/` (0600) and exits when another launcher holds `<data>/nexus/instance.lock` (an OS file lock,
  freed when the process ends, also on a crash). Otherwise it starts with the link.
- **The running launcher:**
  - It reads the inbox every 500 ms and keeps Enshrouded links in memory (`nexus_links`).
  - It emits `kiln://nxm` and brings its window to the front.
  - The UI (desktop shell or Deck) adds each link with `nexus_link_install`.
  - An expired link (`expires` past) is refused: "click Mod Manager Download again".
- **Logs:** they name a link without its query (`nxm://enshrouded/mods/310/files/1001`). The download key is never
  logged.
- **Not supported:** collection links (`nxm://<game>/collections/...`).

Kiln never writes Steam config, nor any other program's files. It writes only its own `.desktop` file, the user's
nxm default, and its own HKCU key.

## Testing
- `cargo test -p kiln-manage --features full nexus` runs 19 tests against a mock Nexus Mods (API, storage, CDN):
  - search over cursor pages; the Kiln rule (old mods and Kiln's own page never checked, EMF classified, verdicts
    cached in memory and on disk, re-checked after `updated_at` changes); batching (`unchecked`); text, sort and
    paging; no key;
  - the key only to the API host; the app headers on every request; `Debug` hides the key;
  - 429 + Retry-After, the budget headers, a bad key, ids that are not numbers; the account keeps the name and premium
    only;
  - details (BBCode to text, images, files, the file Add takes, requirements with Kiln marked); manifest shapes;
  - the nxm flow: a free account refused without the link's key, then the download, the check against the manifest,
    the add to the profile, the record, Installed, Undo; an update found and nothing installed by the check; the
    30-minute cache;
  - refusals: damaged (size mismatch), nothing loadable, 7z, Kiln itself;
  - the handler (remembers Vortex, forwards, gives it back, nothing before Kiln); the real `xdg-mime` in private XDG
    folders; the command-line helpers; the key store (keyring, broken keyring to a 0600 file); the instance lock and
    the inbox.
- `cargo test -p kiln-manage --features full --lib nexus::tests::live -- --ignored --nocapture` uses the real Nexus
  Mods, anonymously and read-only: every Enshrouded mod (237 over 3 cursor pages, 2026-11-07) and Kiln's page parse.
  4 API calls.
- `cargo xwin check -p kiln-manage --features full --target x86_64-pc-windows-msvc` and the same for `-p kiln-launcher`
  build the Credential Manager and registry code.
- `bun x playwright test tests/catalog.spec.ts` (mock mode; the account from `?nexus=free|premium|nokey`; screenshots
  in `.out/catalog/shots/`):
  - desktop: Kiln mods by default, All, Installed, search, the update banner and card; details and the free account's
    file page; an nxm link with "Get" and Undo; an expired link; premium Add and Update; no key; Settings (a bad key
    refused, a good one saved, the opt-in naming Vortex); the Nexus build;
  - Deck: the tab with the pad (rows, X and Y, a mod's page, Add, the added dialog, Get, B); a free account's Open on
    Nexus Mods; an nxm link's dialog.
  - Mock helpers: `__kilnNexusMock.nxm(url)`, `.state()`, `.setUpdate(modId, version)`.
- The real binary, checked by hand: `kiln-launcher --nxm 'nxm://skyrimspecialedition/...'` exits at once and logs
  "another game, not handled" (no handler before). With the instance lock held, an Enshrouded link is queued (0600)
  and the process exits 0. The key is in neither log.

## Manual checklist (real API, your own account)
Use your own key from nexusmods.com/settings/api-keys, never the release key. Run the launcher with
`KILN_DATA_DIR=<scratch>` if you want to keep your real launcher data apart. Report OK or what happened for each line.

1. Settings > Nexus Mods: paste the key, Save. Does it show "Signed in as <you>" with Free or Premium, and "Kept in
   your system keyring"? (Linux: `secret-tool search application kiln-launcher` lists it.) Paste a wrong key: is it
   refused, with the old key kept?
2. Catalog: does "Kiln mods" list only mods that ship a `kiln.mod.json`? Is the count line right? While it says
   "Looking at N more", does the list grow?
3. All Enshrouded: does every mod show, EMF and DLL mods with their word? Search by name and by author.
4. Open a Kiln mod: pictures, description (no BBCode tags left), author, version, endorsements, requirements, files,
   "Add takes this" on the right file.
5. Free account: Download on Nexus Mods opens the file's page with its Mod Manager Download.
6. Settings: turn on "Mod Manager Download links". Does it name your previous manager (Vortex, MO2) or say other
   games are left alone? Linux: `xdg-mime query default x-scheme-handler/nxm` says `kiln-launcher-nxm.desktop`.
   Windows: `reg query HKCU\Software\Classes\nxm\shell\open\command`.
7. On the site, click Mod Manager Download on a Kiln mod (with the launcher open, then again with it closed). Does
   the launcher come to the front and say "Added <name>" with Undo? Is the mod on in the profile? Does
   `<data>/logs/launcher.log` name the link without `key=`?
8. A mod that needs another: does the toast offer "Get <name>", and does Get open it in the catalog?
9. Undo: is the mod out of the profile, with the library copy still there?
10. Another game's Mod Manager Download (e.g. Skyrim): does it reach your previous manager?
11. Premium only: Add in the details downloads and adds without the site.
12. Update: install an older file of a mod (from its Files tab, "Mod Manager Download" on an older version). Does the
    catalog say "Update <version>"? Does Update bring the new one, and only on the click?
13. Turn the links option off. Is the previous manager the nxm handler again? Is the `.desktop` file (or the HKCU
    key) gone?
14. Remove the key: does the catalog go back to listing everything with the "needs your API key" note?
15. Deck mode (`--deck`): the Catalog tab with a controller; X, Y, A on a mod, Add or Open on Nexus Mods, B.
16. Did any step say that Nexus Mods asked Kiln to wait or slow down? Note which one.

## Not done yet
- Collections (`nxm://.../collections/...`) are refused.
- Legacy page-level requirements (the "Requirements" box on old mod pages) are not in API v3. Only file dependencies
  set with Nexus's new requirements system show.
- Mods added from files before the catalog existed are not linked to their Nexus page, so they show no update. A
  catalog Add of the same mod links them from then on.
- Archive formats other than zip (7z, RAR) are refused: Kiln has no decoder for them.
- Text search is local (Nexus's v3 search has no text query): it covers name, summary, author and category of every
  Enshrouded mod, not descriptions.
- No real-account run yet (the checklist above is for the first one). The v3 manifest format was read tolerantly (a
  tree or a flat list of `{path, size, sha256}`); the first real run will show which one Nexus serves.
