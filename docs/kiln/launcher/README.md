# Kiln Launcher docs

| Doc | What |
|---|---|
| [contracts.md](contracts.md) | every format, path, flow, Tauri command, event, error code and crate module (the contracts) |
| [admin-protocol.md](admin-protocol.md) | KAP/1: Kiln.Admin over TCP (framing, handshake, ops, SSH tunnels) |
| [../dedicated-servers.md](../dedicated-servers.md) | for server owners: Kiln on a dedicated server (rented with FTP, own Windows PC, own Linux with kilnctl), checking it, admin, mods, game updates, removing it |
| [releasing.md](releasing.md) | versions, the launcher's self-update (sources, channels, signatures), the release folder and the steps to publish |
| [catalog.md](catalog.md) | the online catalog on Nexus Mods: the Kiln-mod rule, Nexus's rules for apps, adding and updates, Mod Manager Download (nxm://), tests, the real-API checklist; for mod authors: [catalog-authors.md](catalog-authors.md) |
| [deck.md](deck.md) | the Steam Deck UI: how it is chosen, controls, screens, the Steam rule, tests and the Deck tester checklist |
| [perf.md](perf.md) | what the launcher costs idle and per click, the caches and poll rules that keep it low, and the two harnesses that measure it |

Machine-readable contracts:
- `schema/`: JSON Schemas (`*.schema.json`, draft 2020-12), `examples/`, `fixtures/` (resolver, semver, manifest
  parity; play-once/chain INI with `fixtures/ini/kini.py`; kdh1 with `fixtures/dirhash/dirhash.py`; KAP handshake
  vector);
- `native/core/abi/kiln_api.h` (`KILN_PATH_CONFIG/STATE`, `KilnBootInfo` v2), `kiln_native.h`,
  `services/kiln_profile.h`;
- `crates/kiln-manage/src/lib.rs` (modules);
- `launcher/src-tauri/src/commands/` and `launcher/src/api/commands.ts` (command names).

Code:
| Path | What |
|---|---|
| `crates/kiln-manage` | the shared Rust library (kilnctl + launcher) |
| `runner/kilnctl` | the Linux server runner (CLI) |
| `launcher/` | the Kiln Launcher: React 18 + TypeScript + Vite (bun), Tauri 2 backend in `src-tauri/` |
| `mods/Kiln.Admin` | the admin op table (file bridge + KAP/1) |

Checks:
```sh
cargo test --workspace                                      # kiln-manage contract tests, kilnctl
cargo xwin check -p kiln-manage --target x86_64-pc-windows-msvc --all-features
dotnet test managed/tests/Kiln.Host.Tests                    # includes FixtureTests (C# side of parity)
python3 schema/fixtures/ini/kini.py check && python3 schema/fixtures/dirhash/dirhash.py --check
cd launcher && bun run check:schemas && bunx tsc --noEmit && bun run build
```
