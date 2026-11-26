//! The status files Kiln writes into `<game>/kiln/state/` for the launcher (schema/state-*.schema.json):
//! boot.json (kiln-core, end of boot), mods.json (Kiln.Host), native.json (native loader), last-server.json
//! (Kiln.Network on a client), graphics.json (Kiln.Ui on a client, 0.2.0-dev.29.18). All are written atomically (tmp + rename); a file whose pid is not alive is stale.

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
    /// Off, but a DLL of it also loads as a chain proxy (kiln/chain.ini [chain], dinput8-chain.ini): that file (since
    /// 0.2.0-dev.31.12; null or absent otherwise).
    #[serde(default)]
    pub chain_proxy: Option<String>,
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
    /// The server was this game's own world (the player hosting it): nothing it advertises can be missing. Written by
    /// Kiln after 0.2.0-dev.31.17, which checks that the world runs in this game; None in an older file
    /// ([`LastServer::from_own_world`] looks at the server id then).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hosted: Option<bool>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub history: Vec<LastServer>,
}

impl LastServer {
    /// Whether this answer came from the world `game_dir` itself hosted: `hosted` when the file says it (Kiln saw the world
    /// run in this game, or not: a server with this game's id that is not running here is someone else's), else (a file
    /// an older Kiln wrote) the server id is this game's own ([`crate::modpack::own_server_id`]). Up to
    /// 0.2.0-dev.31.17 such a welcome listed the other engines' mods the game ran as missing (netjunk's report,
    /// 2026-11-26).
    pub fn from_own_world(&self, game_dir: &Path) -> bool {
        self.hosted.unwrap_or_else(|| {
            self.server.id.as_deref().is_some_and(|id| crate::modpack::own_server_id(game_dir).as_deref() == Some(id))
        })
    }

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

/// `kiln/state/graphics.json` (Kiln.Ui, 0.2.0-dev.29.18; schema/state-graphics.schema.json): what Kiln's graphics tools
/// do in the client, above all the upscaler behind the game's FSR 3 option. Codes stay strings: a newer runtime may add
/// some, and the launcher shows a code it does not know as "unknown".
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GraphicsState {
    pub format: u32,
    pub pid: u32,
    pub written: String,
    #[serde(default)]
    pub kiln: Option<String>,
    pub upscaler: UpscalerState,
    /// Per vendor folder (`amd`, `intel`): the runtime's version of the files and what of them is in `kiln/vendor/`.
    #[serde(default)]
    pub files: std::collections::BTreeMap<String, VendorFilesSeen>,
    /// The frame cap and DLSS parts, as written (the launcher does not show them yet).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub frame_cap: Option<serde_json::Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dlss: Option<serde_json::Value>,
}

/// `GraphicsState.upscaler`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpscalerState {
    /// `game`, `amd` or `xess` (kiln.json `graphics.upscaler` in use).
    pub chosen: String,
    /// `auto`, `fsr3` or `fsr4`.
    #[serde(default)]
    pub amd_upscaler: String,
    /// `off`, `notFsr`, `gameFsr3`, `amdFsr4`, `amdFsr31`, `xess` or `unknown`.
    pub running: String,
    /// Why the chosen upscaler does not run (None: it runs, or the game's own is chosen); docs/kiln/graphics.md.
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason_number: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default)]
    pub hooked: bool,
    #[serde(default)]
    pub fsr_seen: bool,
    #[serde(default)]
    pub files_ok: bool,
    #[serde(default)]
    pub device_ok: bool,
    #[serde(default)]
    pub fell_back: bool,
    #[serde(default)]
    pub amd_pinned: bool,
    #[serde(default)]
    pub aside: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

/// `GraphicsState.files.<vendor>`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VendorFilesSeen {
    pub version: String,
    /// `present`, `missing` or `changed`.
    pub state: String,
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

/// `kiln/state/graphics.json`; None when there is none, or it is not one of format 1 (a half-written file cannot be
/// read: it is written atomically, so that means another writer).
pub fn read_graphics(game_dir: &Path) -> Option<GraphicsState> {
    read_json::<GraphicsState>(&game_dir.join(crate::paths::game::GRAPHICS_JSON)).ok().flatten().filter(|g| g.format == 1)
}

/// Holds a lock file ([`lock_file`]) until dropped.
#[derive(Debug)]
pub struct HeldLock {
    _file: crate::util::LockedFile,
}

/// Takes the lock of `path` (an exclusive lock on the file, made when missing), across threads and processes: polled every
/// 100 ms until it is free, `canceled()` says stop (`Canceled`) or `wait` is over (`Busy`, with `busy` as the message).
/// The lock goes when the [`HeldLock`] is dropped, and with the process. The file itself stays (an empty file).
pub fn lock_file(path: &Path, wait: std::time::Duration, canceled: &dyn Fn() -> bool, busy: &str) -> Result<HeldLock> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(crate::util::io_at(dir, "creating"))?;
    }
    let file = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(path).map_err(crate::util::io_at(path, "opening"))?;
    let start = std::time::Instant::now();
    loop {
        match file.try_lock() {
            Ok(()) => return Ok(HeldLock { _file: crate::util::LockedFile(file) }),
            Err(std::fs::TryLockError::WouldBlock) => {}
            Err(std::fs::TryLockError::Error(e)) => return Err(crate::util::io_at(path, "locking")(e)),
        }
        if canceled() {
            return Err(Error::new(crate::ErrorCode::Canceled, "canceled"));
        }
        if start.elapsed() >= wait {
            return Err(Error::new(crate::ErrorCode::Busy, busy));
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
}

/// `<game>/kiln/state/cook.lock`: one cook or one start with mods of a game at a time, whoever asks (the launcher,
/// kilnctl, `kiln cook`). The launcher's cook holds it for its whole run, a start with mods until Steam was asked to
/// start the game ([`crate::launch::play_with`]): a cook never writes a mod's files while the game starts with them.
/// A game folder without Kiln has nothing to cook: no lock is taken there (None).
pub fn lock_game(game_dir: &Path, wait: std::time::Duration, canceled: &dyn Fn() -> bool) -> Result<Option<HeldLock>> {
    let kiln = game_dir.join(crate::paths::game::KILN);
    if !kiln.is_dir() {
        return Ok(None);
    }
    let file = kiln.join("state").join("cook.lock");
    crate::engine_mods::refuse_symlink_path(game_dir, &file)?;
    lock_file(&file, wait, canceled, "Kiln is cooking this game's mods, or starting the game: wait until that is done (the task list shows it), then try again.").map(Some)
}

/// Where a cook of a mod folder holds its lock: a file per folder (by its real path) in the system's temp folder, so
/// `kiln cook` and the launcher, two games that load one Library entry, and a start of one game while another cooks
/// that folder, never meet in it ([`lock_cook_folder`]).
pub fn cook_folder_lock_path(dir: &Path) -> std::path::PathBuf {
    use sha2::Digest;
    let real = std::fs::canonicalize(dir).unwrap_or_else(|_| dir.to_path_buf());
    let key = crate::hash::hex(&sha2::Sha256::digest(real.to_string_lossy().as_bytes())[..12]);
    std::env::temp_dir().join("kiln-cook-locks").join(format!("{key}.lock"))
}

/// The lock of a mod folder's cook ([`cook_folder_lock_path`]): held by a cook of that folder, and by a start with
/// mods for each folder it loads until Steam was asked ([`crate::launch::play_with`]).
pub fn lock_cook_folder(dir: &Path, wait: std::time::Duration, canceled: &dyn Fn() -> bool, busy: &str) -> Result<HeldLock> {
    lock_file(&cook_folder_lock_path(dir), wait, canceled, busy)
}

#[cfg(test)]
mod last_server_tests {
    use super::*;

    struct Tmp(std::path::PathBuf);
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn game(tag: &str) -> Tmp {
        let p = std::env::temp_dir().join(format!("km-last-server-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(p.join("kiln/state")).unwrap();
        Tmp(p)
    }

    /// netjunk's last-server.json (2026-11-26): the welcome of his own hosted world, written by 0.2.0-dev.31.17 (no
    /// `hosted`), with the engine mods that game ran as `missing`.
    fn welcome(hosted: Option<bool>) -> LastServer {
        let mut v = serde_json::json!({
            "format": 1, "written": "2026-11-26T13:29:19Z", "outcome": "welcome", "reason": null,
            "server": { "id": "srv-8383ec52951c", "name": null, "address": null, "kiln": "0.2.0-dev.31.17" },
            "modpack": { "url": "https://example.org/pack.zip", "sha256": "a".repeat(64) },
            "required": [{ "id": "creative", "version": "3.2.0", "hash": format!("kdh1:{}", "2".repeat(64)), "kind": "flame" }],
            "truncated": false, "missing": ["creative"],
        });
        if let Some(h) = hosted {
            v["hosted"] = serde_json::json!(h);
        }
        serde_json::from_value(v).unwrap()
    }

    #[test]
    fn hosted_is_read_and_absent_in_an_older_file() {
        assert_eq!(welcome(Some(true)).hosted, Some(true));
        assert_eq!(welcome(Some(false)).hosted, Some(false));
        assert_eq!(welcome(None).hosted, None);
        let v = serde_json::to_value(welcome(None)).unwrap();
        assert!(v.get("hosted").is_none(), "an older file stays without the field when written back");
    }

    // Kiln.Network says hosted: false after checking that no world ran in the game: a dedicated server with the game's
    // own id (a hand-set serverId copied over, or a server started from the same folder) is someone else's server, and
    // its modpack is still offered. Only a file without the field falls back to the id.
    #[test]
    fn hosted_false_is_not_overruled_by_the_server_id() {
        let g = game("said");
        std::fs::create_dir_all(g.0.join("kiln/data/kiln-network")).unwrap();
        std::fs::write(g.0.join(crate::modpack::SERVER_ID_FILE), "srv-8383ec52951c\n").unwrap();
        assert!(!welcome(Some(false)).from_own_world(&g.0));
        assert!(welcome(None).from_own_world(&g.0));
    }

    #[test]
    fn the_hosted_flag_says_the_games_own_world() {
        let g = game("flag");
        assert!(welcome(Some(true)).from_own_world(&g.0));
        assert!(!welcome(Some(false)).from_own_world(&g.0), "no server id here: someone else's server");
    }

    // A file an older Kiln wrote (no `hosted`): the game's own server id says it, as Kiln.Network makes it
    // (network.json serverId, else kiln/data/kiln-network/server-id); reading it never makes one.
    #[test]
    fn an_older_file_is_the_own_world_when_the_server_id_is_this_games() {
        let g = game("id");
        assert!(!welcome(None).from_own_world(&g.0));
        assert!(!g.0.join(crate::modpack::SERVER_ID_FILE).exists(), "reading made no server id");
        std::fs::create_dir_all(g.0.join("kiln/data/kiln-network")).unwrap();
        std::fs::write(g.0.join(crate::modpack::SERVER_ID_FILE), "srv-8383ec52951c\n").unwrap();
        assert!(welcome(None).from_own_world(&g.0));
        std::fs::write(g.0.join(crate::modpack::SERVER_ID_FILE), "srv-0123456789ab\n").unwrap();
        assert!(!welcome(None).from_own_world(&g.0), "a dedicated server's id");
        std::fs::create_dir_all(g.0.join("kiln/config")).unwrap();
        std::fs::write(g.0.join("kiln/config/network.json"), "{ // the owner's\n \"serverId\": \"srv-8383ec52951c\" }").unwrap();
        assert!(welcome(None).from_own_world(&g.0), "network.json serverId wins, as in Kiln.Network");
        let mut no_id = welcome(None);
        no_id.server.id = None;
        assert!(!no_id.from_own_world(&g.0));
    }
}
