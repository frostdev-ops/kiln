//! Dedicated servers the launcher manages: local instances (kilnctl's instance files,
//! `~/.config/kiln/instances/<name>.toml`, Windows `%APPDATA%\Kiln\instances\`) and remote ones reached over SSH.
//! Servers keep today's layout: library mods are synced into `<server>/kiln/mods/<id>/` with a
//! `.kiln-source.json` marker, and switched off with `.disabled`, so other tools and the gate keep working.

pub mod backup;
/// The Console view's source: the capture file of a windowless start, levels, following.
pub mod console;
pub mod control;
/// enshrouded_server.json, the game log, the savegame folder (kilnctl's server.rs).
pub mod game;
pub mod instance;
/// /proc lookups of server processes, UDP ports, signals (kilnctl's procs.rs).
pub mod procs;
/// Steam installs started without Steam: the Proton build, prefix and launch options Steam would use.
pub mod proton;
pub mod query;
pub mod remote;
pub mod run;
pub mod settings;
pub mod steamcmd;

#[cfg(all(test, unix, feature = "server"))]
mod tests;
#[cfg(all(test, windows))]
mod tests_windows;

use crate::manifest::Clients;
use crate::state::BootJson;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ServerState {
    Stopped,
    Starting,
    Running,
    Stopping,
    Unknown,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerStatus {
    pub id: String,
    pub state: ServerState,
    pub pid: Option<u32>,
    pub uptime_s: Option<u64>,
    pub players: Option<u32>,
    pub kiln: Option<String>,
    /// Kiln in the server folder, running or not: the runtime version of its install receipt (`kiln/.install.json`),
    /// else the version of a Kiln `dinput8.dll` put there without the installer (`"unknown"` when it names none).
    /// None when the folder has no Kiln, and for a remote server (not known).
    #[serde(default)]
    pub installed: Option<String>,
    pub boot: Option<BootJson>,
    pub endpoint: Option<crate::admin::protocol::Endpoint>,
}

/// `<server>/kiln/mods/<id>/.kiln-source.json`: where a synced mod came from.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceMarker {
    pub format: u32,
    pub library: crate::library::LibraryRef,
    pub synced: String,
    pub by: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerMod {
    pub id: String,
    pub version: Option<String>,
    /// Folder name under kiln/mods.
    pub folder: String,
    /// No `.disabled` file.
    pub enabled: bool,
    /// From `.kiln-source.json`; None for a mod put there by hand.
    pub source: Option<crate::library::LibraryRef>,
    pub clients: Option<Clients>,
    /// From state/mods.json while the server runs.
    pub state: Option<crate::state::ManagedState>,
    /// kiln (kiln/mods) or native (kiln/native); None reads as kiln.
    #[serde(default)]
    pub kind: Option<crate::manifest::ModKind>,
}

/// Kiln.Network's `kiln/config/network.json` keys the modpack flow adds (existing keys
/// stay as NetworkConfig.cs reads them).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NetworkModpackConfig {
    /// Overrides the generated `kiln/data/kiln-network/server-id`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub server_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub server_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modpack: Option<crate::profile::ModpackRef>,
    /// Send server id/name, modpack and required[] in REJECT/WELCOME (default true).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub advertise: Option<bool>,
}
