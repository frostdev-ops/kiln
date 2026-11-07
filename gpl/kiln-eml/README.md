# kiln-eml (GPL-3.0-or-later)

EML, the Enshrouded Mod Loader by Brabb3l ([kfc-parser](https://github.com/Brabb3l/kfc-parser)), as a Kiln plugin:
`kiln/plugins/kiln-eml.dll` runs unmodified EML mods (`mod.json` + `src/mod.lua`) before the game reads its data.
User and author documentation: [docs/kiln/eml.md](../../docs/kiln/eml.md).

## Licence boundary

Everything under this folder is GPL-3.0-or-later ([LICENSE](LICENSE)), like EML. It is its own Cargo workspace, not a
member of Kiln's MIT workspace, and builds its own artifact. Kiln's MIT code never includes or links code from here; it
loads the DLL and calls its C exports, described by Kiln's own MIT header
[native/core/abi/services/kiln_eml.h](../../native/core/abi/services/kiln_eml.h). Kiln's ABI structs are mirrored in
[kiln-eml/src/plugin.rs](kiln-eml/src/plugin.rs) (MIT headers, compatible with GPL-3).

Kiln's own MIT ports of EML capabilities (managed/Kiln.Api/Eml/) are written from EML's documentation and observed
behaviour, never from this code.

## Layout

| Path | What |
|---|---|
| `eml/` | EML's crates from kfc-parser v0.2.0 (tag `v0.2.0`, commit `16708594d686b030d909f2d835b199d879ea9ad8`): `kfc`, `kfc-base`, `kfc-content`, `kfc-resource`, `mod-loader`, `mod-loader-base`, `mod-loader-lua`, `mod-loader-runtime`. EML's proxies (`dbghelp-proxy`, `dinput8-proxy`), CLI and GUI are not used. |
| `kiln-eml/` | The plugin: `discover` (which mods), `runner` (one fail-soft run), `logging`, `plugin` (the DLL exports), and the host runner `kiln-eml-run`. |
| `build.sh` | `cargo xwin` build to `$KILN_OUT/cmake/bin/plugins/kiln-eml.dll` (static CRT); `--host` also builds `kiln-eml-run`, `--test` runs the tests. |
| `NOTICE.txt` | Shipped next to the DLL as `kiln-eml.NOTICE.txt`, with `LICENSE` as `kiln-eml.LICENSE.txt`. |

## Changes to EML's code

Each is marked `kiln-eml:` in the source.

1. `mod-loader-base`: `ModEnvironment::load_from` / `from_parts` / `mod_paths` and `ModRegistry::load_paths`: an
   environment from an explicit list of mod folders and zips with its own cache folder, instead of `<game>/mods` and
   `<game>/.cache` only. The body of `load_mod` moved to `load_mod_at` (same checks).
2. `mod-loader-lua` `lib.rs`: the change cache tracks exactly the listed mods (`track_mods`), so switching a mod off in
   a Kiln profile re-patches. `RunResult` reports `cached` and `patched`.
3. `mod-loader-lua` `load.rs`: the KFC writer truncates the padding `.dat` containers (`truncate_containers`). Upstream
   keeps a container a previous run filled (a mod that called `create_content`, since removed) while the new header
   lists it as empty, and the game then refuses to start ("Container ..._010.dat has wrong size 4'096 (expected: 0)").
   Found on a lab server on 2026-11-07; worth reporting upstream.
4. Workspace `Cargo.toml`: release builds unwind on panic (EML aborts), so kiln-eml catches a failing run.

## Updating to a new EML release

Copy the eight crates from the new tag over `eml/`, re-apply the four changes (search `kiln-eml:` in the old copy),
update the tag and commit here, in `NOTICE.txt` and in `kiln-eml/src/lib.rs` (`EML_VERSION`), run
`build.sh --host --test` and the probe mod (docs/kiln/eml.md, "Tests").
