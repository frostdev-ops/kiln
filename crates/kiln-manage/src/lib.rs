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
//! | [`paths`] | launcher data dirs, game-side file names |
//! | [`hash`] | kdh1 directory hash, kms1 fingerprint, sha256 |
//! | [`vdf`] | Valve KeyValues text reader |
//! | [`steam`] | Steam roots, libraryfolders, app manifests, compat tool, launch options (read-only) |
//! | [`game`] | game installs: detection, platform, path mode |
//! | [`pe`] | PE exports, `classify_proxy` |
//! | [`proc`] | process list (Linux /proc, Windows Toolhelp) |
//! | [`launch`] | steam:// URIs, the launch flow |
//! | [`winpath`] | host <-> game path translation (Z:, copy mode) |
//! | [`bundle`] | runtime bundles (`kiln-runtime.json`) |
//! | [`install`] | install / upgrade / uninstall / rollback, receipts (`.install.json`) |
//! | [`chain`] | `kiln/chain.ini`, `kiln/chain/once.ini` |
//! | [`manifest`] | `kiln.mod.json` (port of Manifest.cs), `native.json` |
//! | [`semver`] | port of SemVer.cs |
//! | [`resolver`] | port of Resolver.cs |
//! | [`library`] | the content-addressed mod library, import detection |
//! | [`profile`] | profiles (`meta.json`), `profile.json`, activation |
//! | [`playonce`] | `kiln/play-once.ini` |
//! | [`config`] | offline mod configs, `.schema/` snapshots, `config/native.json` |
//! | [`jsonc`] | JSONC CST value edits |
//! | [`saves`] | save slots, save backups, the save editor's commands |
//! | [`logs`] | log files, tail/follow, crash reports |
//! | [`control`] | `kiln/control/*.cmd`, `kiln/control/console/*.cmd` |
//! | [`util`] | paths, UTC time formatting, atomic writes, external commands (from kilnctl) |
//! | [`wine`] | Wine runner selection and environment of server instances (from kilnctl) |
//! | [`state`] | `kiln/state/{boot,mods,native,last-server}.json` |
//! | [`server`] | dedicated server instances, settings, SteamCMD, run, backups, remote |
//! | [`admin`] | KAP/1 protocol, admin client, file bridge, SSH tunnel, endpoint discovery |
//! | [`modpack`] | server modpacks: build, publish, inspect, download, install |
//! | [`gameart`] | game art from the installed client's KFC4 data (save editor icons, UI, fonts) |
//! | [`catalog`] | local game-data catalogs (items, templates, ...) for the linter, VS Code, Kiln.GameIds |
//! | [`lint`] | `kiln lint`: mod files against schema/*.schema.json, ids, references, manifest consistency, licences |

pub mod error;
pub mod task;
pub mod util;

// discovery and system
pub mod game;
pub mod hash;
pub mod launch;
pub mod paths;
pub mod pe;
pub mod proc;
pub mod steam;
pub mod vdf;
pub mod winpath;

// install and profiles
pub mod bundle;
pub mod chain;
pub mod install;
pub mod library;
pub mod manifest;
pub mod playonce;
pub mod profile;
pub mod resolver;
pub mod semver;

// configs, saves, logs
pub mod config;
pub mod control;
pub mod jsonc;
pub mod logs;
pub mod saves;
pub mod state;

// servers, admin, modpacks
pub mod admin;
pub mod modpack;
pub mod server;
pub mod wine;

// game art (feature `gameart`)
#[cfg(feature = "gameart")]
pub mod gameart;

// game-data catalogs (reader in the default build; making one needs feature `gameart`)
pub mod catalog;

// kiln lint (default build, no extra dependency)
pub mod lint;

pub use error::{Error, ErrorCode, Result};

/// This crate's version: the repository's VERSION file (the workspace version mirrors it).
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Steam app ids.
pub const CLIENT_APP_ID: u32 = 1203620;
pub const SERVER_APP_ID: u32 = 2278520;
