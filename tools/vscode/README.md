# Kiln for VS Code

Mod development for Kiln, the Enshrouded mod framework.

- **Mod files:** schemas for `kiln.mod.json`, data packs, cook recipes and Kiln's own files; `kiln lint` on save with quick fixes; completion and hover for game ids (names, GUIDs, icons) from your local game-data catalog.
- **Mods view:** new mod from the templates, build, deploy to a test server, reload, pack. Tasks of type `kiln` with problem matchers for the SDK's KILN0xx/KILN1xx diagnostics.
- **Test servers:** start and stop (kilnctl instances, labs), follow `kiln.log` with PASS/FAIL/[E]/[W] highlighting and clickable mods, files and tests, and a server console over KAP/1.
- **Testing view:** `[KilnTest]` methods run in the server; results, failures and their stack locations show up in place.
- **Game Data view:** browse and search the catalog; copy or insert names and GUIDs.
- **Debugger:** attach to a running test server for breakpoints, stack, locals and evaluate. It uses netcoredbg, which comes with the extension. Stopping the session leaves the server running.
- **Impact assembly** (`.kimp`, `.kpatch`): highlighting, hover and completion for opcodes, natives and types, signature help, and diagnostics from the tools/impact assembler and verifier.

## Install
```sh
cd tools/vscode && npm ci && npm run package          # -> $KILN_OUT/kiln-vscode-<version>.vsix
code --install-extension $KILN_OUT/kiln-vscode-0.1.0.vsix
```
A local build only; it is not published anywhere.

Optional tools: the `kiln` CLI (`cargo install --path crates/kiln-cli`) for lint, catalog and templates; `dotnet`; `python3` and a Kiln checkout (`kiln.root`) for Impact diagnostics and labs.

Guide: `docs/kiln/modding/vscode.md` in the Kiln repository.

MIT licensed; the bundled netcoredbg has its own licences (`dist/netcoredbg/LICENSES.txt`). Enshrouded is a trademark of Keen Games GmbH. Kiln is unofficial.
