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
//! | [`launch`] | steam:// URIs, the launch flow |
//! | [`winpath`] | host <-> game path translation (Z:, copy mode) |
//! | [`bundle`] | runtime bundles (`kiln-runtime.json`) |
//! | [`install`] | install / upgrade / uninstall / rollback, receipts (`.install.json`) |
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
//! | [`found`] | mods in the game folder Kiln did not put there: whether each loads with a profile, and adding one |
//! | [`shroudforge`] | ShroudForge mods next to Kiln: install detection, mod packages, switches, settings, status (owner: sf) |
//! | [`playonce`] | `kiln/play-once.ini` |
//! | [`prestart`] | what kiln-eml is doing before the game's window exists (`kiln/state/eml.json`, phase running) |
//! | [`config`] | offline mod configs, `.schema/` snapshots, `config/native.json` |
//! | [`jsonc`] | JSONC CST value edits |
//! | [`saves`] | save slots, save backups, the save editor's commands |
//! | [`logs`] | log files, tail/follow, crash reports |
//! | [`report`] | support reports: summary, findings, redacted files, the zip (`zip`) |
//! | [`control`] | `kiln/control/*.cmd`, `kiln/control/console/*.cmd` |
//! | [`util`] | paths, UTC time formatting, atomic writes, external commands (from kilnctl) |
//! | [`wine`] | Wine runner selection and environment of server instances (from kilnctl) |
//! | [`state`] | `kiln/state/{boot,mods,native,last-server}.json` |
//! | [`server`] | dedicated server instances, settings, SteamCMD, run, backups, remote |
//! | [`admin`] | KAP/1 protocol, admin client, file bridge, SSH tunnel, endpoint discovery |
//! | [`modpack`] | server modpacks: build, publish, inspect, download, install |
//! | [`cooktools`] | the cook tools this build carries (`cooktools`): unpack, the Python to run them, the command |
//! | [`gameart`] | game art from the installed client's KFC4 data (save editor icons, UI, fonts) |
//! | [`catalog`] | local game-data catalogs (items, templates, ...) for the linter, VS Code, Kiln.GameIds |
//! | [`lint`] | `kiln lint`: mod files against schema/*.schema.json, ids, references, manifest consistency, licences |
//! | [`nexus`] | the online mod catalog on Nexus Mods, Mod Manager Download (nxm://) links, the API key store |

pub mod error;
pub mod task;
pub mod util;

// discovery and system
pub mod game;
pub mod filecache;
pub mod hash;
pub mod hostenv;
pub mod launch;
pub mod paths;
pub mod data_folder;
pub mod data_folder_move;
pub mod engines;
pub mod pe;
pub mod proc;
pub mod steam;
pub mod vdf;
pub mod winpath;

// install and profiles
pub mod bundle;
pub mod chain;
pub mod compat;
pub mod install;
pub mod archive;
pub mod library;
pub mod manifest;
pub mod playonce;
pub mod prestart;
pub mod profile;
pub mod notices;
pub mod quick;
pub mod engine_mods;
pub mod found;
pub mod own_loaders;
pub mod shroudforge;
pub mod resolver;
pub mod semver;

// configs, saves, logs
pub mod config;
pub mod control;
pub mod jsonc;
pub mod logs;
pub mod report;
pub mod saves;
pub mod state;

// servers, admin, modpacks
pub mod admin;
pub mod modpack;
pub mod server;
pub mod wine;

// game art (feature `gameart`)
#[cfg(feature = "cooktools")]
pub mod cooktools;

#[cfg(feature = "gameart")]
pub mod gameart;

// game-data catalogs (reader in the default build; making one needs feature `gameart`)
pub mod catalog;

// kiln lint (default build, no extra dependency)
pub mod lint;

// the online catalog on Nexus Mods (network calls need feature `http`)
pub mod nexus;

pub use error::{Error, ErrorCode, Result};

/// This crate's version: the repository's VERSION file (the workspace version mirrors it).
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Steam app ids.
pub const CLIENT_APP_ID: u32 = 1203620;
pub const SERVER_APP_ID: u32 = 2278520;
