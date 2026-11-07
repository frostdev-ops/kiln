//! The status files Kiln writes into `<game>/kiln/state/` for the launcher (schema/state-*.schema.json):
//! boot.json (kiln-core, end of boot), mods.json (Kiln.Host), native.json (native loader), last-server.json
//! (Kiln.Network on a client). All are written atomically (tmp + rename); a file whose pid is not alive is stale.

use crate::manifest::{Clients, RunSide, Side};
use crate::profile::{BoundServer, ModpackRef};
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExeInfo {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub sha256: Option<String>,
    #[serde(default)]
    pub verified: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayOnceReport {
    /// none | no-mods | profile | ignored-expired | ignored-undeletable | ignored-server | ignored-invalid
    pub mode: String,
    #[serde(default)]
    pub nonce: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProfileMode {
    Legacy,
    Active,
    Once,
    NoMods,
    Invalid,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KindCounts {
    #[serde(default)]
    pub kiln: u32,
    #[serde(default)]
    pub native: u32,
    #[serde(default)]
    pub plugin: u32,
    #[serde(default)]
    pub emf: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub eml: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub shroudforge: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub set: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub greeneye: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub flame: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub shroudtopia: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub enshwick: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub shroudlink: u32,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub ember: u32,
}

fn is_zero(value: &u32) -> bool { *value == 0 }

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootProfile {
    #[serde(with = "profile_mode")]
    pub mode: ProfileMode,
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub generation: u64,
    #[serde(default)]
    pub file: Option<String>,
    #[serde(default)]
    pub error: Option<String>,
    #[serde(default)]
    pub config_dir: Option<String>,
    #[serde(default)]
    pub data_dir: Option<String>,
    #[serde(default)]
    pub counts: Option<KindCounts>,
}

mod profile_mode {
    use super::ProfileMode;
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(m: &ProfileMode, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(match m {
            ProfileMode::Legacy => "legacy",
            ProfileMode::Active => "active",
            ProfileMode::Once => "once",
            ProfileMode::NoMods => "no-mods",
            ProfileMode::Invalid => "invalid",
        })
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<ProfileMode, D::Error> {
        Ok(match String::deserialize(d)?.as_str() {
            "legacy" => ProfileMode::Legacy,
            "active" => ProfileMode::Active,
            "once" => ProfileMode::Once,
            "no-mods" => ProfileMode::NoMods,
            _ => ProfileMode::Invalid,
        })
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootPaths {
    #[serde(default)]
    pub game: Option<String>,
    #[serde(default)]
    pub kiln: Option<String>,
    #[serde(default)]
    pub mods: Option<String>,
    #[serde(default)]
    pub logs: Option<String>,
    #[serde(default)]
    pub data: Option<String>,
    #[serde(default)]
    pub control: Option<String>,
    #[serde(default)]
    pub config: Option<String>,
    #[serde(default)]
    pub state: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ChainSource {
    Chain,
    Emf,
    Profile,
}

/// KILN_CHAIN_* (boot.json chain[].status, native.json dlls[].status).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ChainStatus {
    Loaded,
    Failed,
    Missing,
    Refused,
    Duplicate,
    Skipped,
}

/// KILN_CHAIN_REFUSED_* (KilnChainEntry.detail).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum RefusalDetail {
    #[serde(rename = "self")]
    SelfProxy,
    System,
    Kiln,
    #[serde(rename = "shroudforge")]
    ShroudForge,
    NotDll,
    Outside,
    TooMany,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootChainEntry {
    pub path: String,
    pub source: ChainSource,
    pub status: ChainStatus,
    #[serde(default)]
    pub detail: Option<RefusalDetail>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootPlugin {
    pub file: String,
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    /// loaded | failed | skipped
    pub status: String,
    #[serde(default)]
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeSummary {
    #[serde(default)]
    pub loaded: u32,
    #[serde(default)]
    pub failed: u32,
    #[serde(default)]
    pub pending: u32,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClrReport {
    #[serde(default)]
    pub started: bool,
    #[serde(default)]
    pub error: Option<String>,
}

/// `kiln/state/boot.json`
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootJson {
    pub format: u32,
    pub kiln: String,
    #[serde(default)]
    pub abi: Option<u32>,
    pub pid: u32,
    pub written: String,
    #[serde(default)]
    pub boot_ms: Option<f64>,
    pub side: RunSide,
    #[serde(default)]
    pub exe: Option<ExeInfo>,
    #[serde(default)]
    pub policy: Option<String>,
    pub hooks: bool,
    #[serde(default)]
    pub safe_idle: Option<String>,
    #[serde(default)]
    pub play_once: Option<PlayOnceReport>,
    pub profile: BootProfile,
    #[serde(default)]
    pub paths: BootPaths,
    #[serde(default)]
    pub chain: Vec<BootChainEntry>,
    #[serde(default)]
    pub services: Vec<String>,
    #[serde(default)]
    pub plugins: Vec<BootPlugin>,
    #[serde(default)]
    pub native: Option<NativeSummary>,
    #[serde(default)]
    pub clr: Option<ClrReport>,
    #[serde(default)]
    pub errors: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EntrySource {
    Profile,
    Dropin,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ManagedState {
    Pending,
    Loaded,
    Disabled,
    Failed,
    Skipped,
    Unloaded,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagedMod {
    pub id: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub folder: Option<String>,
    #[serde(default)]
    pub source: Option<EntrySource>,
    pub state: ManagedState,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub load_index: Option<u32>,
    #[serde(default)]
    pub side: Option<Side>,
    #[serde(default)]
    pub clients: Option<Clients>,
    #[serde(default)]
    pub faults: Option<u32>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StateProfileRef {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub generation: Option<u64>,
    #[serde(default)]
    pub mode: Option<String>,
}

/// `kiln/state/mods.json`
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModsJson {
    pub format: u32,
    pub pid: u32,
    pub written: String,
    pub generation: u64,
    #[serde(default)]
    pub side: Option<RunSide>,
    #[serde(default)]
    pub api: Option<String>,
    #[serde(default)]
    pub profile: Option<StateProfileRef>,
    pub mods: Vec<ManagedMod>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum NativeModState {
    Pending,
    Loaded,
    Failed,
    Disabled,
    Skipped,
    Policy,
    Unsupported,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeDll {
    pub file: String,
    pub status: ChainStatus,
    #[serde(default)]
    pub error: Option<i64>,
    #[serde(default)]
    pub ms: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeInit {
    #[serde(default)]
    pub export: Option<String>,
    #[serde(default)]
    pub result: Option<i64>,
    #[serde(default)]
    pub fault: Option<String>,
    #[serde(default)]
    pub ms: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeModStatus {
    pub id: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub folder: Option<String>,
    #[serde(default)]
    pub source: Option<EntrySource>,
    #[serde(default)]
    pub phase: Option<crate::manifest::NativePhase>,
    pub state: NativeModState,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub notes: Vec<String>,
    #[serde(default)]
    pub load_index: Option<u32>,
    #[serde(default)]
    pub dlls: Vec<NativeDll>,
    #[serde(default)]
    pub init: Option<NativeInit>,
}

/// `kiln/state/native.json`
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeJson {
    pub format: u32,
    pub pid: u32,
    pub written: String,
    /// always | verified-only | never
    pub policy: String,
    #[serde(default)]
    pub phase: Option<crate::manifest::NativePhase>,
    pub mods: Vec<NativeModStatus>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum JoinOutcome {
    Rejected,
    Welcome,
    Vanilla,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerRef {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub address: Option<String>,
    #[serde(default)]
    pub kiln: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RequiredMod {
    pub id: String,
    pub version: String,
    pub hash: String,
    /// None for `kiln` from older servers, or a newer kind preserved in `unknown_kind`.
    pub kind: Option<crate::manifest::ModKind>,
    /// A newer server's kind. Kept distinct from an omitted kind (legacy Kiln), and round-tripped on the wire.
    pub unknown_kind: Option<String>,
    pub name: Option<String>,
}

impl<'de> Deserialize<'de> for RequiredMod {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        #[derive(Deserialize)]
        struct Wire {
            id: String,
            version: String,
            hash: String,
            #[serde(default)]
            kind: Option<String>,
            #[serde(default)]
            name: Option<String>,
        }
        let w = Wire::deserialize(d)?;
        let kind = w.kind.as_deref().and_then(crate::manifest::ModKind::parse);
        let unknown_kind = if kind.is_none() { w.kind } else { None };
        Ok(Self { id: w.id, version: w.version, hash: w.hash, kind, unknown_kind, name: w.name })
    }
}

impl Serialize for RequiredMod {
    fn serialize<S: serde::Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        #[derive(Serialize)]
        struct Wire<'a> {
            id: &'a str,
            version: &'a str,
            hash: &'a str,
            #[serde(skip_serializing_if = "Option::is_none")]
            kind: Option<&'a str>,
            #[serde(skip_serializing_if = "Option::is_none")]
            name: Option<&'a str>,
        }
        Wire { id: &self.id, version: &self.version, hash: &self.hash,
            kind: self.unknown_kind.as_deref().or(self.kind.map(|k| k.as_str())), name: self.name.as_deref() }.serialize(s)
    }
}

/// `kiln/state/last-server.json`
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LastServer {
    pub format: u32,
    pub written: String,
    pub outcome: JoinOutcome,
    #[serde(default)]
    pub reason: Option<String>,
    pub server: ServerRef,
    #[serde(default)]
    pub modpack: Option<ModpackRef>,
    #[serde(default)]
    pub required: Vec<RequiredMod>,
    #[serde(default)]
    pub truncated: bool,
    #[serde(default)]
    pub missing: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub history: Vec<LastServer>,
}

impl LastServer {
    /// The profile binding a modpack install creates from this.
    pub fn bound_server(&self) -> Option<BoundServer> {
        Some(BoundServer {
            id: self.server.id.clone()?,
            name: self.server.name.clone(),
            address: self.server.address.clone(),
            modpack: self.modpack.clone(),
        })
    }
}

fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<Option<T>> {
    match std::fs::read(path) {
        Ok(bytes) => Ok(Some(serde_json::from_slice(&bytes)?)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(Error::from(e)),
    }
}

pub fn read_boot(game_dir: &Path) -> Result<Option<BootJson>> {
    read_json(&game_dir.join(crate::paths::game::BOOT_JSON))
}

pub fn read_mods(game_dir: &Path) -> Result<Option<ModsJson>> {
    read_json(&game_dir.join(crate::paths::game::MODS_JSON))
}

pub fn read_native(game_dir: &Path) -> Result<Option<NativeJson>> {
    read_json(&game_dir.join(crate::paths::game::NATIVE_JSON))
}

pub fn read_last_server(game_dir: &Path) -> Result<Option<LastServer>> {
    read_json(&game_dir.join(crate::paths::game::LAST_SERVER_JSON))
}
