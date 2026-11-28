//! kiln-manage: everything the Kiln Launcher and kilnctl do to games, servers and mods, as a library.
//!
//! Contracts: `docs/kiln/launcher/contracts.md` (formats, flows, command names) and `schema/` (JSON Schemas,
//! fixtures). Serialized shapes listed in contracts.md are stable: change them together with the contract and its
//! readers. Public functions that are not implemented yet return [`Error::not_implemented`].
//!
//! | module | what |
//! |---|---|
//! | [`error`] | `Error {code, message, detail}`, [`ErrorCode`] |
//! | [`task`] | progress reporting for long operations |
//! | [`paths`] | launcher data dirs, game-side file names, the environment as a value (`DataEnv`), locating through the pointer |
//! | [`data_folder`] | the data folder as a whole: what a folder is, its inventory, the pointer, the lock, the target check of a move |
//! | [`data_folder_move`] | moving the data folder, using another one, starting fresh, finishing or undoing a move at the next start |
//! | [`hash`] | kdh1 directory hash, kms1 fingerprint, sha256 |
//! | [`filecache`] | sha256s and parsed files remembered while the file is unchanged (the launcher keeps the sha256s across starts) |
//! | [`vdf`] | Valve KeyValues text reader |
//! | [`steam`] | Steam roots, libraryfolders, app manifests, compat tool, launch options (read-only) |
//! | [`game`] | game installs: detection, platform, path mode |
//! | [`pe`] | PE exports, `classify_proxy` |
//! | [`engines`] | every mod engine Kiln knows: proxy names, markers, Library detection of their mods, loaders |
//! | [`proc`] | process list (Linux /proc, Windows Toolhelp) |
//! | [`debug`] | debugger targets: the core's descriptor, the checked Windows identity, the resolver, session state (`kiln debug`) |
//! | [`launch`] | steam:// URIs, the launch flow |
//! | [`direct`] | starting the client directly (a folder no Steam library knows): per-install start mode, Proton/Wine and prefix on Linux, the command, whether Kiln knows the exe's build |
//! | [`start_progress`] | what a start is doing (`kiln/state/progress.json` and the launcher's own steps), the history of starts, the time left |
//! | [`winpath`] | host <-> game path translation (Z:, copy mode) |
//! | [`bundle`] | runtime bundles (`kiln-runtime.json`) |
//! | [`install`] | install / upgrade / uninstall / rollback, receipts (`.install.json`) |
//! | [`blocked`] | a DLL or exe the install could not write because Windows refused it (an antivirus, another program): `detail.blocked`, the report's finding from launcher.log |
//! | [`chain`] | `kiln/chain.ini`, `kiln/chain/once.ini` |
//! | [`compat`] | closed DLL mods: compat rules, the last session's hooks.json overlaps, chain.ini [late] plans |
//! | [`manifest`] | `kiln.mod.json` (port of Manifest.cs), `native.json` |
//! | [`semver`] | port of SemVer.cs |
//! | [`resolver`] | port of Resolver.cs |
//! | [`library`] | the content-addressed mod library, import detection |
//! | [`archive`] | 7z (in-process) and rar (a tool already on the PC) packages, unpacked under the import limits |
//! | [`profile`] | profiles (`meta.json`), `profile.json`, activation |
//! | [`notices`] | which activation notices the launcher has shown per game (`installs/<id>.notices-seen.json`) |
//! | [`quick`] | the simple flow: import + add to the active profile + apply, the default profile "My mods", undo |
//! | [`engine_mods`] | other engines' mods in the launcher: importers, placing SF/SET mods, settings, plugin status (owner: wire) |
//! | [`game_files`] | native mods placed in the game folder itself: a plugin loader the game loads (`powrprof.dll` + `plugins\`), Ultimate ASI Loader and `.asi` plugins |
//! | [`found`] | mods in the game folder Kiln did not put there: whether each loads with a profile, and adding one |
//! | [`shroudforge`] | ShroudForge mods next to Kiln: install detection, mod packages, switches, settings, status (owner: sf) |
//! | [`sfi_backend`] | kiln.json `shroudforge.backend` (hosted, installed, kiln): read and written as the core decides it at boot |
//! | [`sfi_release`] | the release hold of Kiln's interpreter as this build ships it: the gate rows `kiln-sfi.interpreter` / `kiln-sfi.sf18` and the sf18 veto |
//! | `sfi_scan` | feature `sfi-scan`: the pre-select inventory of `<game>/mods` ShroudForge packages a `kiln` launch plan chooses from |
//! | `sfi_plan` | feature `sfi-scan`: the launch-plan producer, `kiln/launch-plans/<plan id>.json` for a `kiln` start |
//! | `sfi_inspection` | feature `sfi-scan`: kiln-eml's read-only archive inspector as the plan producer runs and reads it |
//! | `sfi_activation` | feature `sfi-scan`: the activation journal of a kiln-mode activation, `kiln/state/sfi-activation.json`: the plan, chain.ini and the game-side copies staged so that a stop ends in the earlier or the new activation, whole, and the planned start's check |
//! | [`sfi_routing`] | where a game's ShroudForge-format mods go: main's routes, or `<game>/mods` for Kiln's interpreter when kiln.json selects it (active with feature `sfi-scan`) |
//! | [`playonce`] | `kiln/play-once.ini` |
//! | [`prestart`] | what kiln-eml is doing before the game's window exists (`kiln/state/eml.json`, phase running) |
//! | [`config`] | offline mod configs, `.schema/` snapshots, `config/native.json` |
//! | [`jsonc`] | JSONC CST value edits |
//! | [`saves`] | save slots, save backups, the save editor's commands |
//! | [`logs`] | log files, tail/follow, crash reports |
//! | [`tick_compare`] | before/after tick-cost report from two `tick-stats.jsonl` files (T12) |
//! | [`report`] | support reports: summary, findings, redacted files, the zip (`zip`) |
//! | [`control`] | `kiln/control/*.cmd`, `kiln/control/console/*.cmd` |
//! | [`util`] | paths, UTC time formatting, atomic writes, external commands (from kilnctl) |
//! | [`wine`] | Wine runner selection and environment of server instances (from kilnctl) |
//! | [`registry_restart`] | what the registry left waiting for a restart: `kiln/state/registry.json`'s RestartRequired rows |
//! | [`state`] | `kiln/state/{boot,mods,native,last-server}.json` |
//! | [`registry_plan`] | the registry stage's plans and a rollback to the previous (last good) one: `kiln/data/content/registry/` |
//! | [`server`] | dedicated server instances, settings, SteamCMD, run, backups, remote |
//! | [`admin`] | KAP/1 protocol, admin client, file bridge, SSH tunnel, endpoint discovery |
//! | [`modpack`] | server modpacks: build, publish, inspect, download, install |
//! | [`cook`] | cooking a mod folder with them, what "needs a cook" means, the record per game (`cooktools`) |
//! | [`cooktools`] | the cook tools this build carries (`cooktools`): unpack, the Python to run them, the command |
//! | [`gameart`] | game art from the installed client's KFC4 data (save editor icons, UI, fonts) |
//! | [`catalog`] | local game-data catalogs (items, templates, ...) for the linter, VS Code, Kiln.GameIds |
//! | [`lint`] | `kiln lint`: mod files against schema/*.schema.json, ids, references, manifest consistency, licences |
//! | [`nexus`] | the online mod catalog on Nexus Mods, Mod Manager Download (nxm://) links, the API key store |
//! | [`net`] | the HTTP layer of `kiln publish` and the update check: fixed hosts per provider, public addresses only, no redirects, a fake transport for tests |
//! | [`publish`] | `kiln publish`: the artifact policy and audit of a mod zip (`zip`) |
//! | [`release_binding`] | `kiln-release.json`: which mod, version and zip a release holds |
//! | [`updates`] | update checks for installed mods: source receipts, one status per mod from every provider; a notice and a link, never a download |
//! | [`vendor`] | vendor upscaler files the player asks the launcher for (AMD FSR, Intel XeSS): the shipped manifest with its pins and licences, downloads checked by size and SHA-256, `<game>/kiln/vendor/`, removal |
//! | [`community`] | the launcher's optional Discord features: the relay client, the session key, sign-in, the public post of a report, known problems, testers, Rich Presence |

pub mod error;
pub mod task;
pub mod util;

// discovery and system
pub mod game;
pub mod filecache;
pub mod hash;
pub mod hostenv;
pub mod launch;
pub mod direct;
pub mod start_progress;
pub mod stopped_start;
pub mod paths;
pub mod data_folder;
pub mod data_folder_move;
pub mod engines;
pub mod dllmod;
pub mod pe;
pub mod proc;
pub mod debug;
pub mod steam;
pub mod vdf;
pub mod winpath;

// install and profiles
pub mod bundle;
pub mod chain;
pub mod compat;
pub mod install;
pub mod blocked;
pub mod archive;
pub mod library;
pub mod manifest;
pub mod playonce;
pub mod prestart;
pub mod profile;
pub mod notices;
pub mod quick;
pub mod kept;
pub mod settings_files;
pub mod mod_settings;
pub mod config_orphans;
pub(crate) mod durable;
pub mod engine_mods;
pub mod emf_deps;
pub mod game_files;
pub mod restore_data;
pub mod found;
pub mod own_loaders;
pub mod shroudforge;
pub mod sf_startup;
pub mod sfi_backend;
pub mod sfi_release;
pub mod sfi_routing;
#[cfg(feature = "sfi-scan")]
pub mod sfi_scan;
#[cfg(feature = "sfi-scan")]
pub mod sfi_inspection;
#[cfg(feature = "sfi-scan")]
pub mod sfi_plan;
#[cfg(feature = "sfi-scan")]
pub mod sfi_activation;
// the check before Play; its warnings need sfi-scan, a build without has none
pub mod sfi_launch;
// the ShroudForge engine row read from kiln-sfi's sfi.json in kiln mode
pub mod sfi_state;
pub(crate) mod sf_paths;
pub mod resolver;
pub mod semver;

// configs, saves, logs
pub mod config;
pub mod control;
pub mod jsonc;
pub mod logs;
pub mod registry_restart;
pub mod report;
pub mod tick_compare;
pub mod saves;
pub mod state;
pub mod registry_plan;

// servers, admin, modpacks
pub mod admin;
pub mod modpack;
pub mod server;
pub mod wine;

// game art (feature `gameart`)
#[cfg(feature = "cooktools")]
pub mod cook;
#[cfg(feature = "cooktools")]
pub mod cooktools;
#[cfg(feature = "cooktools")]
pub mod boot_snapshot;

#[cfg(feature = "gameart")]
pub mod gameart;

// game-data catalogs (reader in the default build; making one needs feature `gameart`)
pub mod catalog;

// kiln lint (default build, no extra dependency)
pub mod lint;

// the online catalog on Nexus Mods (network calls need feature `http`)
pub mod nexus;
pub mod net;
pub mod release_binding;
pub mod publish;
pub mod updates;
pub mod vendor;
pub mod community;
#[cfg(all(feature = "zip", feature = "http"))]
pub mod crucible;

pub use error::{Error, ErrorCode, Result};

/// This Kiln's version: the repository's VERSION file as built (build.rs), so a release's own version
/// (0.2.0-dev.31.1), not the tree's placeholder (0.2.0-dev). Mod and modpack `kiln` ranges are checked against it.
pub const VERSION: &str = env!("KILN_BUILD_VERSION");

/// Steam app ids.
pub const CLIENT_APP_ID: u32 = 1203620;
pub const SERVER_APP_ID: u32 = 2278520;
