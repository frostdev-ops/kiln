# Server modpacks

How a player gets a server's mods: the server says what it needs, the launcher offers it, the player agrees, the
launcher downloads, checks and installs it into a profile. Formats: [contracts.md](contracts.md) 5.8 and 5.9,
`schema/kiln-modpack.schema.json`, `schema/state-last-server.schema.json`. Handshake side: [../network.md](../network.md)
"Server modpacks". Code: `crates/kiln-manage/src/modpack.rs`, `launcher/src-tauri/src/commands/{server,modpack}.rs`,
`launcher/src/pages/servers/{ServerModpackPage,ModpackOffer}.tsx`.

## Server admin: build and publish (`/servers/:id/modpack`)
1. **What players need**: the server's mods with `clients: "mod"` (the same rule as Kiln.Network's advertised list:
   `kiln/mods/*` and `kiln/native/*`, not `.disabled`, not failed), each with the kdh1 of its folder.
2. **Build** (`server_modpack_build(serverId, name, version)`, a task): a deterministic zip in
   `<data>/modpacks/built/<serverId>/<name>-<version>.zip`: `kiln-modpack.json` first, then `<kind>/<id>/...` sorted
   bytewise, one fixed time (the newest file's), mode 0644, deflate level 6. The same mods give the same bytes and
   sha256. Dot entries are skipped and symlinks refused (kdh1's rules). The manifest has the server's id and name (the
   id file is created in Kiln.Network's format if the server never ran), `kiln: ">=<the server's Kiln>"`, and each mod's
   kind, id, version, name, path, kdh1, clients, side. The build record goes to
   `<server>/kiln/data/kiln-network/modpack-build.json`.
3. **Publish** (`server_modpack_publish(serverId, url)`): the admin uploads the zip anywhere players can reach (https)
   and enters the URL. The launcher writes `modpack {url, sha256, name, version, size}` into `kiln/config/network.json`
   by editing only that value (comments and other keys stay) and asks a running server to re-read it (`network_reload`).
   From then on every REJECT and WELCOME carries the modpack.

## Player: the offer and the install
1. Kiln.Network writes `kiln/state/last-server.json` when a server answers the join. The launcher watches it
   (`kiln://last-server`); `modpack_pending(gameId)` returns it when it is worth an offer: a refusal, or a welcome that
   still lists missing mods, from a server that advertises a modpack, unless the user dismissed that same modpack
   (`modpack_dismiss`, kept in `<data>/modpacks/dismissed.json` per game and server).
2. **The consent modal** (`ModpackOffer`, opened from Play's refusal banner): server name and id, the refusal reason,
   the modpack's name, version, URL, size and sha256, what is missing (`modpack_inspect`, which downloads nothing),
   whether it holds native code, a "this runs code on your PC" warning, and a checkbox the user must tick. http URLs are
   refused unless Settings allow http modpacks.
3. **Install** (`modpack_install(gameId, {source: {url, sha256}, consent: true, allowHttp?, activate?})`, a task):
   - `consent` must be true (else `consent_required`; the Tauri command checks it again, and `allowHttp` needs the
     setting);
   - download with ureq (rustls) into `<data>/modpacks/downloads/<sha256>.zip`: https only unless allowed, at most 2 GB,
     the sha256 computed while streaming (mismatch: `hash_mismatch`, nothing kept), cancelable, progress in
     `kiln://task`; a cached download with the right sha256 is reused;
   - `verify`: the zip's sha256, `kiln-modpack.json` (format 1, ids, paths `<kind>/<id>` or `plugin/<file>.dll`, no
     duplicates), every entry inside a listed mod, no symlink entries, no absolute or `..` paths, each mod's kdh1
     recomputed from the zip's entries;
   - when last-server.json is about this server: every mod the server requires must be in the pack with the same hash;
   - unpack into `<data>/modpacks/staging/<random>/`, kdh1 checked again on disk, `library::import`;
   - the profile bound to the server id (reused), else a new "Server: <name>", with exactly the pack's mods and
     `server {id, name, address, modpack}`;
   - unless `activate: false`: a save backup (reason `modpack`), then activation. The modal then offers Launch.

## Checks done
`cargo test -p kiln-manage --all-features --lib modpack`: deterministic build (two builds, same bytes), verify, a
tampered file (`hash_mismatch`) and an unlisted entry (`refused`), the required list against the manifest, extraction,
publish keeping comments, consent and https refusals, download with sha check and cleanup over a local http server.
Lab: the offline required list equals what the running server advertises (`lab_required_matches_server`).

Real client (build 1285601, a lab with `modSync: refuse`, net-test + netsample
required, the pack served by `python3 -m http.server --bind 127.0.0.1`): the first join was refused
(`refusing Alder ... net-test 0.1.0 (missing); netsample 1.0.0 (missing)`) and last-server.json said `rejected` with the
modpack and `missing [net-test, netsample]`; the launcher's refusal banner opened the consent modal; the install
downloaded the pack, imported `net-test/0.1.0+5fcff2b7` and `netsample/1.0.0+697edb89`, made the profile
"Server: Kiln lab mylab", backed up the saves and activated it; the next start booted the profile
(`mods: 2 loaded`, drop-ins off) and the rejoin got WELCOME (`Alder joined with Kiln 0.2.0-dev: 2 mod(s), 5 shared
channel(s)`; last-server.json `welcome`, `missing []`, the refusal in history).
