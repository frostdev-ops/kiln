# The online catalog (Nexus Mods)

The official **0.2.0-dev.21** build (like dev.20) uses `KILN_NEXUS_ENABLED=0`: no Catalog, Nexus account/settings or `nxm://`
handler. Download archives in a browser and import them manually. Kiln removes what an older build (0.2.0-dev.19)
left behind, and only Kiln's own: the personal API key and the nxm registration (see "Leftovers of older builds").
Your OAuth sign-in, if any, and other programs' handlers are not touched. The enabled implementation described below is
for a future approved configuration, not this build.

The launcher's Catalog lists Enshrouded's mods on Nexus Mods, Kiln mods first. It shows each mod's details, adds a
mod to the active profile (with Undo), takes "Mod Manager Download" (`nxm://`) links, and tells you when an installed
mod has a newer file. Desktop: the Catalog page (`/catalog`) and Settings > Nexus Mods. Deck mode: the Catalog tab.
Mod authors: [catalog-authors.md](catalog-authors.md).

Code:
- `crates/kiln-manage/src/nexus/`: the API client, the catalog, downloads, nxm parsing, the handler registration and
  the OAuth token store (unit tests in `tests.rs` against a mock Nexus Mods).
- `launcher/src-tauri/src/commands/nexus.rs`: the `nexus_*` commands, the link inbox, `kiln://nxm`.
- `launcher/src/api/nexus.ts` and `mockNexus.ts`; `src/pages/CatalogPage.tsx`; `src/components/NexusSettings.tsx`;
  `src/shell/catalogInstall.tsx` (the shared Add flow and the nxm listener); `src/deck/screens/Catalog.tsx`.
- Contracts: [contracts.md](contracts.md) sections 3 (`<data>/nexus/`), 9 (`nexus_*`, the types), 10 (`kiln://nxm`)
  and 12.

## Which mods are Kiln mods
A mod counts as a Kiln mod when one of its active files (main, update, optional, miscellaneous) has a `kiln.mod.json`
in its newest version's archive, at the root or in a folder. Kiln fetches versions for at most eight active files and
checks manifests for the three most recently updated listed files.

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
- A look uses one files API call, up to eight version API calls, and up to three public storage-manifest reads.
- At most 12 mods are looked at per search. The page asks again while `unchecked` > 0.
- The answers are kept in `<data>/nexus/verdicts.json` per mod and its `updated_at`, so a mod is looked at again only
  after its page changes. Failed manifest reads are reported and retried; they are never cached as a negative verdict.
- The mod list itself is kept 10 minutes. Enshrouded had 237 visible mods (3 search pages) on 2026-11-07.

File metadata needs an authorized account; public archive manifests are fetched without credentials. Signed out, the
catalog lists Enshrouded mods anonymously and offers browser sign-in to identify Kiln mods.

## Disabled builds

`KILN_NEXUS_ENABLED=0` disables the whole integration at compile time. Backend `app_info.nexusEnabled` controls
both UIs; no frontend-only environment override can enable it. Catalog routes, account/settings controls and Nexus
navigation are hidden. Nexus IPC, provider requests, token/keyring access, downloads, nxm opt-in, inbox processing
and refresh are refused before side effects. Direct nxm callbacks are never queued or downloaded (see below). This
mode requires no OAuth registration for an official release.

### Leftovers of older builds
Version 0.2.0-dev.19 let you paste a personal API key and could register Kiln for `nxm://` links. A build with Nexus
off cleans up after that at every start. It names only Kiln's own entries and writes nothing to the log but the facts
below; the key is never printed, logged or sent.

- **The personal API key.** In every build, Kiln deletes `<data>/nexus/apikey.json` and the keyring entry
  `nexusmods-api-key` (Linux: application `kiln-launcher`; Windows: Credential Manager `Kiln Launcher/nexusmods-api-key`).
  The log says `nexus: removed the stored Nexus personal API key` when it found one. The OAuth entry is a different
  slot and stays. The keyring is read only to learn that the old entry exists. Once a keyring lookup has worked (found
  nothing, or removed the entry), `<data>/state/nexus-personal-key-retired` is written and later starts check only the
  file, so a locked keyring does not ask to be unlocked at every start. A lookup that failed (keyring locked and the
  prompt dismissed, no keyring service running) writes no marker; the next start asks again. On Linux, Kiln confirms
  an empty slot with `secret-tool search --all`, which never prompts.
- **The nxm handler.** If `<data>/nexus/handler.json` says Kiln registered itself, a build with Nexus off gives the
  links back at start:
  - Kiln is still the system's nxm handler: the manager before it (Vortex, Mod Organizer 2, ...) is restored the way
    turning the option off did it (Linux: the nxm line in `mimeapps.list` and Kiln's `.desktop` file; Windows: the
    `HKCU\Software\Classes\nxm` values), then `handler.json` is removed. The log says
    `nexus: Nexus Mods is off in this build: gave the nxm links back to Vortex`. If no manager was set before Kiln,
    Kiln's registration is simply removed.
  - Another program has the links since: Kiln only drops its own record. The system's association is not changed.
  - A restore that fails keeps the record, so the next start tries again.
- **A link that still arrives.** A browser may start `kiln-launcher --nxm <link>` before the handler is given back.
  Kiln gives the handler back first, then hands that link to the manager before it (any game's link). Kiln never
  hands a link to itself, and on Windows it starts only a local `.exe` (no script host such as `cmd.exe` or `mshta.exe`). With no such
  manager recorded, the window opens with a message: the integration is off and how to reset the handler (Linux:
  `xdg-mime default <your mod manager>.desktop x-scheme-handler/nxm`; Windows: Settings > Apps > Default apps >
  Choose defaults by link type > NXM). Nothing is queued and nothing is downloaded.

A build with Nexus on does none of the handler steps: a registration there is your choice.

## Nexus's rules for apps, and how Kiln follows them
- **Naming the app:** every request sends `Application-Name: Kiln Launcher`, `Application-Version: <launcher version>`
  and `User-Agent: Kiln-Launcher/<version> (<os>; <arch>)`. No user name, machine name or anything personal.
- **OAuth authorization:**
  - Settings and Deck Settings open Nexus Mods in the system browser. The public desktop client uses authorization
    code flow with PKCE S256 and unpredictable per-attempt state, following the
    [Nexus guide](https://modding.wiki/en/api/oauth2-guide).
  - The backend binds the exact approved IPv4 loopback callback before opening the browser. Callback path and state
    must match; duplicate parameters are refused. Codes and PKCE verifiers never pass through the frontend. A
    completed callback is consumed once; cancellation, sign-out and timeout close the attempt.
  - Token exchange and refresh use `https://users.nexusmods.com/oauth/token`. The access token authenticates API
    calls with `Authorization: Bearer`; the account endpoint validates account name and premium status. The app
    does not trust an unverified JWT payload for authorization.
  - OAuth tokens are stored in the OS keyring: Secret Service via `secret-tool` on Linux, Credential Manager on
    Windows. A host without a backend uses `<data>/nexus/oauth.json` (0600 on Unix). Backend failures are surfaced
    without silently downgrading to a file. `KILN_NO_KEYRING=1` explicitly skips the keyring.
  - Expiry triggers refresh and persistence of the rotated pair, serialized across launcher instances by a private lock. Revoked refresh credentials sign the
    account out. Sign-out clears backend state and stored tokens; an older sign-in attempt in the running launcher cannot restore them.
  - No personal API key is accepted. Retired `apikey.json` and legacy keyring entries are removed, not reused.
  - Credentials never go to settings, frontend state, logs, profiles or modpacks. Authenticated requests and token
    exchanges do not follow redirects; downloads and archive manifests receive no account authorization.

- **Rate limits:**
  - One API request at a time per client, at least 150 ms apart, including receipt of the previous response.
  - The `x-rl-hourly-remaining` / `x-rl-daily-remaining` headers are read. Requests stop when either drops under 20, so the
    user's other tools keep some budget. Budgets expire at the reported reset time (one hour/day fallback).
  - A 429 blocks the client for its `Retry-After` delay or HTTP date (60 seconds when unavailable).
  - Transport reads time out after 60 seconds without progress, including downloads.
  - Caches keep the use low: the list for 10 minutes, verdicts per `updated_at`, update checks 30 minutes per file.
- **Downloads:**
  - Premium accounts download through the API (`download-link` without a key).
  - Free accounts must download through the website. For them Add opens the file's page
    (`?tab=files&file_id=<id>&nmm=1`, which shows its Mod Manager Download). That button makes an nxm link carrying
    `key` + `expires`, which the launcher takes (below). The launcher never works around this.
- **The Nexus Mods build:** it downloads mods the user picks, the same as the direct build. It never downloads or
  replaces itself ([releasing.md](releasing.md), "The two builds").

### Public-client registration and release gate

Nexus must approve Kiln's own public client ID and callback URI. Builds take these public values from
`KILN_NEXUS_OAUTH_CLIENT_ID` and `KILN_NEXUS_OAUTH_REDIRECT_URI` at compile time. No client secret is needed or shipped.
Absent registration, sign-in is unavailable; there is no borrowed test registration or personal-key fallback.
The proposed callback for registration is `http://127.0.0.1:8089/callback`; it remains subject to Nexus approval.

The registration request has been sent to Nexus support with the application name, description, dark-background logo,
source link and callback URI. Approval, client ID, scopes and redirect configuration remain pending. An enabled Nexus release remains blocked until approval and real-account checks on the
supported platforms complete. A release compiled with `KILN_NEXUS_ENABLED=0` disables the whole integration and
may ship while approval is pending. Mock-provider tests demonstrate local flow behavior, not Nexus approval.

## Adding a mod
1. **The file:**
   - From the catalog: the file shown in the details as "Add takes this". That is the Kiln file, else the primary
     file, else the newest main file.
   - From an nxm link: the file the link names.
2. **The download:** download links (4 hours), then the first link that works (https, at most 2 GB) into
   an exclusive random folder under `<data>/nexus/downloads/<mod>-<file>/`. The returned file must belong to the
   requested mod page; IDs are validated before constructing local paths.
3. **The check:**
   - The file must be a zip or a DLL. A 7z or RAR is refused: "download it by hand and unpack it".
   - Every file that Nexus's manifest lists with a size must have that size in the zip, and its sha256 must match when
     the manifest has one. Missing listed files or a failed manifest fetch refuse the install. An upload with no
     manifest URL can still be installed with `noManifest: true`. Paths are matched as they are, then without a top folder.
   - Before import, every ZIP entry is streamed and checked for CRC, unsafe paths, symlinks/special files and
     duplicates. Both declared and actual expanded contents are limited to 2 GiB total, with at most 100,000 entries.
   - `library::detect` must find something Kiln loads.
4. **Adding it:** `quick::add` imports it into the library and adds it to the active profile ("My mods" for a
   first-time player). It also writes the game's `profile.json`, the same as a dropped file (contracts.md section 7,
   "Add a mod").
5. **The record:** the mod, its file and version and the library entries go to `<data>/nexus/installs.json`. Nexus
   profile mutations and record writes are serialized across launcher processes; installing invalidates stale update checks.
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
  - It writes only the nxm default in the highest-priority existing desktop-specific config mimeapps.list, or
    `$XDG_CONFIG_HOME/mimeapps.list` as fallback, and runs `update-desktop-database` when available. Other schemes
    and their text are preserved.
- **Windows:** `HKCU\Software\Classes\nxm` (`URL:NXM Protocol`, `URL Protocol`, `DefaultIcon`,
  `shell\open\command` = `"<launcher>" --nxm "%1"`).
- **The handler before Kiln:**
  - On Linux this is `xdg-mime query default`. On Windows it is the HKCU command, else HKLM's.
  - The previous association snapshot is saved privately in `<data>/nexus/handler.json` before changing the OS.
    Registration toggles use a cross-process lock and failed registration rolls back. Settings names the previous
    handler ("Vortex", "Mod Organizer 2").
  - Well-formed links for other games go to it unchanged, as one argument. Unsafe URI characters are refused;
    Windows also refuses script interpreter handlers.
- **Turning it off:**
  - Linux restores the original nxm default list, including whether it was inherited, and the original Kiln desktop
    file bytes (or removes that file if absent before registration). Unrelated subsequent MIME edits are preserved.
  - Windows restores the overwritten HKCU values with their exact original types, bytes and absence; only empty
    keys created by Kiln are removed. Machine-wide and unrelated schemes remain untouched.
  - A later user-selected manager is left in control. Old format-1 registrations lack a complete original snapshot;
    information already discarded by those versions cannot be reconstructed.
- **Another program:** if one takes the links back later, Settings says so ("turn this off and on again").
- **One launcher:** the browser starts a second launcher process for each link. That process writes the link to
  `<data>/nexus/inbox/` (owner-only directory; Unix 0700, files 0600; protected Windows DACL) and exits when another launcher holds `<data>/nexus/instance.lock` (an OS file lock,
  freed when the process ends, also on a crash). Otherwise it starts with the link.
- **The running launcher:**
  - Only the instance-lock owner reads the inbox every 500 ms and keeps Enshrouded links in memory (`nexus_links`).
    Reads refuse symlinks, non-private files and messages over 16 KiB, and revalidate each URL.
  - It emits `kiln://nxm` and brings its window to the front.
  - The UI (desktop shell or Deck) adds each link with `nexus_link_install` once the game and Kiln are ready.
    Switching UIs preserves results/Undo; Deck queues successive reports. Invalid game selections do not consume links.
  - An expired link (`expires` past) is refused: "click Mod Manager Download again".
- **Logs:** they name a link without its query (`nxm://enshrouded/mods/310/files/1001`). The download key is never
  logged.
- **Not supported:** collection links (`nxm://<game>/collections/...`).

Kiln never writes Steam config, nor any other program's files. It writes only its own `.desktop` file, the user's
nxm default, and its own HKCU key.

## Testing
- `cargo test -p kiln-manage --features full nexus` runs the Nexus regression tests against mock Nexus Mods (API, storage, CDN):
  - search over cursor pages; the Kiln rule (old mods and Kiln's own page never checked, EMF classified, verdicts
    cached in memory and on disk, re-checked after `updated_at` changes); batching (`unchecked`); text, sort and
    paging while signed out;
  - bearer authorization only to the API host; app headers on every request; `Debug` redacts credentials;
  - 429 + Retry-After, the budget headers, refused authorization, ids that are not numbers; the account keeps the name and premium
    only;
  - details (BBCode to text, images, files, the file Add takes, requirements with Kiln marked); manifest shapes;
  - the nxm flow: a free account refused without the link's key, then the download, the check against the manifest,
    the add to the profile, the record, Installed, Undo; an update found and nothing installed by the check; the
    30-minute cache;
  - refusals: damaged (size mismatch), nothing loadable, 7z, Kiln itself;
  - the handler (remembers Vortex, forwards, gives it back, nothing before Kiln); the real `xdg-mime` in private XDG
    folders; the command-line helpers; the token store (keyring failure, migration and private file mode); the instance lock and
    the inbox; unsafe ZIPs, expansion limits, parallel downloads/records, exact handler restore, malformed links,
    rate concurrency, large versions, pagination failures and retry behavior.
- `tools/test-nexus-disabled.sh` builds the tests with `KILN_NEXUS_ENABLED=0` and runs the `disabled_nexus` ones: the
  tripwires (a build with Nexus off never reaches the provider, the keyring, the handler or the disk), the removal of
  the personal key (a fake keyring, and the real keyring adapter against a stand-in `secret-tool`), and the nxm
  leftovers (fakes, and the real `xdg-mime` in private XDG folders). The flag is read when the code is compiled, so a
  plain `cargo test` runs them against a build with Nexus on, where the tripwires return at once. Do not run the other
  `nexus` tests with the flag off: they test the enabled build. `tools/release/checks.sh` runs the script when a
  release is built with `KILN_NEXUS_ENABLED=0`. None of these tests touches your keyring, `mimeapps.list` or registry.
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
Use Kiln's approved OAuth registration and your own Nexus account. `KILN_DATA_DIR=<scratch>` separates launcher
files, but the OS keyring and nxm association still belong to your user account. Report each result separately on
Windows and Linux/Deck; do not print tokens or authorization codes.

1. Settings > Nexus Mods: Sign in, complete browser consent, and return to Kiln. Does it show your account with Free
   or Premium? Deny consent and cancel an attempt: does it stay signed out? Sign out, then sign in again. Check token
   expiry/refresh and revocation using Nexus's authorized-applications page. Sign out during a pending callback and
   verify that the old callback cannot sign the account back in.
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
14. Sign out: does the catalog offer sign-in and continue anonymous browsing? Is stored authorization removed?
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
- Public OAuth application registration and a real-account run are still pending (the checklist above is for the first one). The v3 manifest format was read tolerantly (a
  tree or a flat list of `{path, size, sha256}`); the first real run will show which one Nexus serves.
