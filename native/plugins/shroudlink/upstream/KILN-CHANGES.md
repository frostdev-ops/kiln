# Kiln changes to shroudlink

`upstream/` is [bjck/shroudlink](https://github.com/bjck/shroudlink) v0.8.0 (commit `bfeebd07`, MIT, `LICENSE`):
the crates `server`, `common`, `link` and `sdk` from its `mod/` workspace, and its `Cargo.lock`. Every change is
marked `kiln:` in the code. Without the cargo feature `kiln` the server crate builds as upstream's
`steam_api64.dll` proxy again; `../build.sh` builds it with `--features kiln` as `kiln-shroudlink.dll`.

| File | Change |
|---|---|
| `server/src/kiln.rs` | new (Kiln's own, MIT): `kiln_plugin_early` (EML data mods to kiln-eml), `kiln_plugin_main`/`on_ready` (profile mod list, step aside, start), log mirror, `kiln/state/shroudlink.json`, host tests |
| `server/Cargo.toml` | feature `kiln` |
| `server/build.rs` | with `kiln`, the Steam stubs are not exported |
| `server/src/lib.rs` | with `kiln`: no `DirectInput8Create`/crash-handler exports, no steam proxy, no start from `DllMain`, Kiln's crash handler kept; `init` split into `start(load_mods)` returning whether this copy claimed the process |
| `server/src/plugins.rs` | the loader reads several roots (folders or single DLLs) with a chosen shadow folder (`load_from`); `OUTCOMES` records each DLL's result; `touch_for_test` searches the roots |
| `common/src/log.rs` | `set_sink`: every line also goes to Kiln's log |
| `common/src/scan.rs` | `find` falls back to `.text` as the exe file holds it when the live code no longer matches (another hook, e.g. Kiln.Gameplay's on the event dispatcher, already replaced the prologue), so shroudlink's hook chains onto it |

Updating: copy the four crates and `Cargo.lock` of the new tag over `upstream/`, re-apply the `kiln:` changes (they
are small and local), run `../build.sh --test` and the lab self-test (docs/kiln/engines/shroudlink.md).
