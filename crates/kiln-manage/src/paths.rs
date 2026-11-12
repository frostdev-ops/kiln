//! Where things live. Launcher data: `~/.local/share/kiln/` (`$XDG_DATA_HOME/kiln`), Windows `%LOCALAPPDATA%\Kiln\`,
//! `$KILN_DATA_DIR` overrides both. Layout (contracts.md, "Launcher data"):
//! `library/{kiln,native,plugin,emf}/<id>/<version>+<hash8>/`, `profiles/<pid>/{meta.json,config/,data/,save-backups/}`,
//! `installs/<installId>.json`, `runtime/`, `schema-cache/`, `backups/`, `logs/`, `settings.json`.
//!
//! The environment the root is found from is a value ([`DataEnv`]): [`DataEnv::from_process`] reads it once, tests
//! build it by hand, and [`DataDirs::locate`] is `DataEnv::from_process().locate()`.

use crate::manifest::ModKind;
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::ffi::OsString;
use std::path::{Path, PathBuf};

/// Game-side paths, relative to the game folder (the folder with the exe). '/' separated.
pub mod game {
    pub const KILN: &str = "kiln";
    pub const PROFILE: &str = "kiln/profile.json";
    pub const PROFILE_ONCE: &str = "kiln/profile.once.json";
    pub const PLAY_ONCE: &str = "kiln/play-once.ini";
    pub const CHAIN: &str = "kiln/chain.ini";
    pub const CHAIN_DIR: &str = "kiln/chain";
    pub const CHAIN_ONCE: &str = "kiln/chain/once.ini";
    pub const EMF_CHAIN: &str = "dinput8-chain.ini";
    pub const RECEIPT: &str = "kiln/.install.json";
    pub const BACKUPS: &str = "kiln/backups";
    pub const STATE: &str = "kiln/state";
    pub const BOOT_JSON: &str = "kiln/state/boot.json";
    pub const MODS_JSON: &str = "kiln/state/mods.json";
    pub const NATIVE_JSON: &str = "kiln/state/native.json";
    pub const LAST_SERVER_JSON: &str = "kiln/state/last-server.json";
    pub const CONFIG: &str = "kiln/config";
    pub const DATA: &str = "kiln/data";
    pub const LOGS: &str = "kiln/logs";
    pub const PROXY_LOG: &str = "kiln/logs/proxy.log";
    pub const CONTROL: &str = "kiln/control";
    /// Copy mode: `kiln/profiles/<pid>/{kiln,native,plugin,emf}/<id>/` and `config/`.
    pub const COPY_PROFILES: &str = "kiln/profiles";
    /// Drop-in folders per kind (legacy layout / profile `dropins`).
    pub const DROPIN_KILN: &str = "kiln/mods";
    pub const DROPIN_NATIVE: &str = "kiln/native";
    pub const DROPIN_PLUGIN: &str = "kiln/plugins";
    pub const DROPIN_EMF: &str = "emf/mods";
    /// Server side: a launcher-synced mod folder carries this marker (`kiln/mods/<id>/.kiln-source.json`).
    pub const SOURCE_MARKER: &str = ".kiln-source.json";
    /// A mod folder with this file is skipped by the loaders.
    pub const DISABLED_MARKER: &str = ".disabled";
    /// Kiln.Admin: `kiln/data/kiln-admin/{config.json,endpoint.json,admin-token}`.
    pub const ADMIN_DIR: &str = "kiln/data/kiln-admin";
}

/// The launcher's data folders.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataDirs {
    pub root: PathBuf,
}

impl DataDirs {
    pub fn at(root: impl Into<PathBuf>) -> Self {
        DataDirs { root: root.into() }
    }

    /// `$KILN_DATA_DIR`, else the folder the pointer file in the default root names, else the platform default
    /// ([`DataEnv::locate`] over the process environment). A broken pointer is an error (`ErrorCode::DataFolder`).
    pub fn locate() -> Result<Self> {
        DataEnv::from_process().locate().map(|l| l.dirs)
    }

    pub fn library(&self) -> PathBuf {
        self.root.join("library")
    }
    pub fn profiles(&self) -> PathBuf {
        self.root.join("profiles")
    }
    pub fn installs(&self) -> PathBuf {
        self.root.join("installs")
    }
    pub fn runtime(&self) -> PathBuf {
        self.root.join("runtime")
    }
    pub fn schema_cache(&self) -> PathBuf {
        self.root.join("schema-cache")
    }
    pub fn backups(&self) -> PathBuf {
        self.root.join("backups")
    }
    pub fn logs(&self) -> PathBuf {
        self.root.join("logs")
    }
    pub fn settings(&self) -> PathBuf {
        self.root.join("settings.json")
    }

    /// `library/<kind>/<id>/<version>+<hash8>` (hash8 = the first 8 hex chars of the kdh1, or of the sha256 for a
    /// plugin DLL).
    pub fn library_entry(&self, kind: ModKind, id: &str, version: &str, hash: &str) -> Result<PathBuf> {
        let h8 = crate::hash::hash8(hash).ok_or_else(|| Error::invalid(format!("not a kdh1/sha256: {hash}")))?;
        Ok(self.library().join(kind.as_str()).join(id).join(format!("{version}+{h8}")))
    }

    /// `profiles/<pid>`.
    pub fn profile(&self, pid: &str) -> PathBuf {
        self.profiles().join(pid)
    }
}

/// How the data root in use was found.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RootSource {
    /// `$KILN_DATA_DIR`: a pointer file would not be read.
    Env,
    /// The pointer file in the default root names it (`data_folder::POINTER`).
    Pointer,
    /// The platform default ([`DataEnv::default_root`]).
    Default,
}

/// The data root, how it was found, and the default root (where the pointer and a move's journal live; None only when
/// `KILN_DATA_DIR` is set and the platform default cannot be told).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Located {
    pub dirs: DataDirs,
    pub source: RootSource,
    pub default_root: Option<PathBuf>,
}

/// What is wrong when the data root cannot be told ([`DataEnv::locate_checked`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ProblemKind {
    /// The platform default cannot be told (`HOME` or `LOCALAPPDATA` is not set).
    NoDefault,
    /// The pointer file cannot be read.
    Unreadable,
    /// The pointer file is not JSON, or not a pointer.
    Unparsable,
    /// The pointer file has a larger `format`: a newer launcher wrote it.
    NewerFormat,
    /// The pointer names a relative path (or one with `\\?\`).
    NotAbsolute,
    /// The folder the pointer names is not there.
    Missing,
    /// The pointer names a file or a link, not a folder.
    NotAFolder,
    /// The folder is there but cannot be read.
    FolderUnreadable,
    /// The folder is not a Kiln data folder (`data_folder::recognize`).
    NotKiln,
}

/// Why the data root cannot be told: the pointer file, the folder it names, and one sentence.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocateProblem {
    pub kind: ProblemKind,
    /// The pointer file (`<default root>/data-folder.json`).
    pub pointer: Option<PathBuf>,
    /// The folder it names, when it could be read.
    pub path: Option<PathBuf>,
    pub reason: String,
}

impl LocateProblem {
    /// As the crate's error: `not_found` for [`ProblemKind::NoDefault`] (as before dev.26), else `data_folder` with
    /// `detail: {kind, pointer, path}`.
    pub fn into_error(self) -> Error {
        if self.kind == ProblemKind::NoDefault {
            return Error::not_found(self.reason);
        }
        let detail = serde_json::json!({ "kind": self.kind, "pointer": self.pointer, "path": self.path });
        Error::new(crate::ErrorCode::DataFolder, self.reason).with_detail(detail)
    }
}

/// The environment the launcher's folders are found from, as a value: [`DataEnv::from_process`] reads the process
/// environment, tests build it by hand (`DataEnv { home: Some(..), ..Default::default() }`). `windows` picks the
/// platform's rules (the process: `cfg!(windows)`), so the Windows rules also run in a test on Linux; paths are still
/// joined with the host's separator. Each field is the variable's value as the process has it (None: not set).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DataEnv {
    pub windows: bool,
    /// `KILN_DATA_DIR`: the data root itself, when not empty.
    pub kiln_data_dir: Option<OsString>,
    /// `LOCALAPPDATA` (Windows): the default root's parent, and `state_dir()`'s.
    pub local_app_data: Option<OsString>,
    /// `XDG_DATA_HOME` (not Windows), when not empty.
    pub xdg_data_home: Option<OsString>,
    /// `HOME`: the default root's base when `XDG_DATA_HOME` is not set (not Windows), and `util::home()` there.
    pub home: Option<OsString>,
    /// `USERPROFILE`: `util::home()` on Windows.
    pub user_profile: Option<OsString>,
    /// `XDG_STATE_HOME`, when absolute (`util::state_dir()`).
    pub xdg_state_home: Option<OsString>,
    /// `APPDATA` (Windows, `util::config_dir()`).
    pub app_data: Option<OsString>,
    /// `XDG_CONFIG_HOME`, when absolute (`util::config_dir()`).
    pub xdg_config_home: Option<OsString>,
}

impl DataEnv {
    /// The process environment (each variable read once).
    pub fn from_process() -> DataEnv {
        let v = std::env::var_os;
        DataEnv {
            windows: cfg!(windows),
            kiln_data_dir: v("KILN_DATA_DIR"),
            local_app_data: v("LOCALAPPDATA"),
            xdg_data_home: v("XDG_DATA_HOME"),
            home: v("HOME"),
            user_profile: v("USERPROFILE"),
            xdg_state_home: v("XDG_STATE_HOME"),
            app_data: v("APPDATA"),
            xdg_config_home: v("XDG_CONFIG_HOME"),
        }
    }

    /// The platform default data root, whatever `KILN_DATA_DIR` says: Windows `%LOCALAPPDATA%\Kiln`, else
    /// `$XDG_DATA_HOME/kiln` (when not empty) or `$HOME/.local/share/kiln`. It is also where the pointer file lives.
    pub fn default_root(&self) -> Result<PathBuf> {
        if self.windows {
            let base = self.local_app_data.as_ref().ok_or_else(|| Error::not_found("LOCALAPPDATA is not set"))?;
            return Ok(Path::new(base).join("Kiln"));
        }
        if let Some(x) = self.xdg_data_home.as_ref().filter(|d| !d.is_empty()) {
            return Ok(Path::new(x).join("kiln"));
        }
        let home = self.home.as_ref().ok_or_else(|| Error::not_found("HOME is not set"))?;
        Ok(Path::new(home).join(".local/share/kiln"))
    }

    /// The data root in use: `$KILN_DATA_DIR` (when not empty), else the folder the pointer file in the default root
    /// names (`data_folder::POINTER`), else [`DataEnv::default_root`]. A pointer that cannot be read or parsed, has a
    /// larger `format`, or names a folder that is missing, unreadable or not a Kiln data folder is an error with code
    /// `data_folder` (the path and the reason in it), never the default root and never an empty folder. Reading costs
    /// the pointer file and a look at the folder's marker (its entries only when it has none).
    pub fn locate(&self) -> Result<Located> {
        self.locate_checked().map_err(LocateProblem::into_error)
    }

    /// [`DataEnv::locate`] that says what is wrong instead of failing (the launcher's dialog for a broken pointer).
    pub fn locate_checked(&self) -> std::result::Result<Located, LocateProblem> {
        let default = self.default_root();
        if let Some(d) = self.kiln_data_dir.as_ref().filter(|d| !d.is_empty()) {
            return Ok(Located { dirs: DataDirs::at(d), source: RootSource::Env, default_root: default.ok() });
        }
        let default = default.map_err(|e| LocateProblem { kind: ProblemKind::NoDefault, pointer: None, path: None, reason: e.message })?;
        match crate::data_folder::read_pointer(&default)? {
            None => Ok(Located { dirs: DataDirs::at(&default), source: RootSource::Default, default_root: Some(default) }),
            Some(p) => {
                crate::data_folder::check_pointed(&p.path, self, &default.join(crate::data_folder::POINTER))?;
                Ok(Located { dirs: DataDirs::at(p.path), source: RootSource::Pointer, default_root: Some(default) })
            }
        }
    }

    /// `util::home()` from this environment: `USERPROFILE` on Windows, else `HOME`; `/` when unset.
    pub fn home_dir(&self) -> PathBuf {
        let v = if self.windows { &self.user_profile } else { &self.home };
        v.as_ref().map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
    }

    fn xdg(&self, var: &Option<OsString>, fallback: &str) -> PathBuf {
        match var {
            Some(v) if Path::new(v).is_absolute() => PathBuf::from(v),
            _ => self.home_dir().join(fallback),
        }
    }

    /// `util::state_dir()` from this environment: Windows `%LOCALAPPDATA%\Kiln\state` (inside the default root: it is
    /// not launcher data and never moves with it), else `$XDG_STATE_HOME/kiln` or `~/.local/state/kiln`.
    pub fn state_dir(&self) -> PathBuf {
        if self.windows && let Some(a) = &self.local_app_data {
            return PathBuf::from(a).join("Kiln").join("state");
        }
        self.xdg(&self.xdg_state_home, ".local/state").join("kiln")
    }

    /// `util::config_dir()` from this environment: Windows `%APPDATA%\Kiln`, else `$XDG_CONFIG_HOME/kiln` or
    /// `~/.config/kiln` (kilnctl's instance files).
    pub fn config_dir(&self) -> PathBuf {
        if self.windows && let Some(a) = &self.app_data {
            return PathBuf::from(a).join("Kiln");
        }
        self.xdg(&self.xdg_config_home, ".config").join("kiln")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    /// `DataDirs::locate` as it was before dev.26 (a446e223), with the variables from `get` and the platform as a
    /// value: the reference the new code must match.
    fn locate_before(get: &dyn Fn(&str) -> Option<OsString>, windows: bool) -> Result<DataDirs> {
        if let Some(d) = get("KILN_DATA_DIR").filter(|d| !d.is_empty()) {
            return Ok(DataDirs::at(d));
        }
        if windows {
            let base = get("LOCALAPPDATA").ok_or_else(|| Error::not_found("LOCALAPPDATA is not set"))?;
            Ok(DataDirs::at(Path::new(&base).join("Kiln")))
        } else {
            if let Some(x) = get("XDG_DATA_HOME").filter(|d| !d.is_empty()) {
                return Ok(DataDirs::at(Path::new(&x).join("kiln")));
            }
            let home = get("HOME").ok_or_else(|| Error::not_found("HOME is not set"))?;
            Ok(DataDirs::at(Path::new(&home).join(".local/share/kiln")))
        }
    }

    fn env_of(vars: &HashMap<&str, &str>, windows: bool) -> DataEnv {
        let g = |k: &str| vars.get(k).map(OsString::from);
        DataEnv {
            windows,
            kiln_data_dir: g("KILN_DATA_DIR"),
            local_app_data: g("LOCALAPPDATA"),
            xdg_data_home: g("XDG_DATA_HOME"),
            home: g("HOME"),
            user_profile: g("USERPROFILE"),
            xdg_state_home: g("XDG_STATE_HOME"),
            app_data: g("APPDATA"),
            xdg_config_home: g("XDG_CONFIG_HOME"),
        }
    }

    fn same(a: Result<DataDirs>, b: Result<DataDirs>) -> bool {
        match (a, b) {
            (Ok(a), Ok(b)) => a == b,
            (Err(a), Err(b)) => a.code == b.code && a.message == b.message,
            _ => false,
        }
    }

    /// Every branch of the old `locate` (both platforms), against `DataEnv::locate` built by hand.
    #[test]
    fn locate_matches_the_code_before_dev26_in_every_branch() {
        let cases: &[&[(&str, &str)]] = &[
            &[],
            &[("KILN_DATA_DIR", "/scratch/env")],
            &[("KILN_DATA_DIR", "relative/env"), ("HOME", "/h")],
            &[("KILN_DATA_DIR", ""), ("HOME", "/h")],
            &[("KILN_DATA_DIR", ""), ("XDG_DATA_HOME", "/x"), ("HOME", "/h"), ("LOCALAPPDATA", "C:\\Users\\Jörg Müller\\AppData\\Local")],
            &[("XDG_DATA_HOME", ""), ("HOME", "/h")],
            &[("XDG_DATA_HOME", "rel"), ("HOME", "/h")],
            &[("HOME", "")],
            &[("LOCALAPPDATA", "")],
            &[("LOCALAPPDATA", "D:\\")],
            &[("USERPROFILE", "C:\\Users\\a"), ("APPDATA", "C:\\Users\\a\\AppData\\Roaming")],
        ];
        for case in cases {
            let vars: HashMap<&str, &str> = case.iter().copied().collect();
            for windows in [false, true] {
                let get = |k: &str| vars.get(k).map(OsString::from);
                let new = env_of(&vars, windows).locate().map(|l| l.dirs);
                assert!(same(new, locate_before(&get, windows)), "{case:?} windows={windows}");
                let source = env_of(&vars, windows).locate().map(|l| l.source).ok();
                let env_set = vars.get("KILN_DATA_DIR").is_some_and(|v| !v.is_empty());
                if let Some(s) = source {
                    assert_eq!(s, if env_set { RootSource::Env } else { RootSource::Default }, "{case:?}");
                }
            }
        }
    }

    /// `DataDirs::locate()` over the real process environment gives what the old code gives over it, and the
    /// environment value gives what `util` gives (the test runs set these variables to scratch folders).
    #[test]
    fn locate_state_and_config_match_the_process() {
        let get = |k: &str| std::env::var_os(k);
        assert!(same(DataDirs::locate(), locate_before(&get, cfg!(windows))));
        let env = DataEnv::from_process();
        assert_eq!(env.state_dir(), crate::util::state_dir());
        assert_eq!(env.config_dir(), crate::util::config_dir());
        assert_eq!(env.home_dir(), crate::util::home());
    }

    #[test]
    fn default_root_and_state_dir_follow_the_platform_rules() {
        let linux = DataEnv { home: Some("/h".into()), ..Default::default() };
        assert_eq!(linux.default_root().unwrap(), Path::new("/h/.local/share/kiln"));
        assert_eq!(linux.state_dir(), Path::new("/h/.local/state/kiln"));
        assert_eq!(linux.config_dir(), Path::new("/h/.config/kiln"));
        let xdg = DataEnv { xdg_state_home: Some("relative".into()), xdg_data_home: Some("/d".into()), ..linux.clone() };
        assert_eq!(xdg.default_root().unwrap(), Path::new("/d/kiln"));
        assert_eq!(xdg.state_dir(), Path::new("/h/.local/state/kiln"), "a relative XDG_STATE_HOME is ignored");
        // KILN_DATA_DIR does not change the default root (the pointer's place)
        let env = DataEnv { kiln_data_dir: Some("/e".into()), ..linux };
        assert_eq!(env.default_root().unwrap(), Path::new("/h/.local/share/kiln"));
        assert_eq!(env.locate().unwrap().source, RootSource::Env);
        // Windows: %LOCALAPPDATA%\Kiln, with the state folder inside it
        let win = DataEnv { windows: true, local_app_data: Some("/lad".into()), app_data: Some("/ad".into()), ..Default::default() };
        assert_eq!(win.default_root().unwrap(), Path::new("/lad").join("Kiln"));
        assert_eq!(win.state_dir(), Path::new("/lad").join("Kiln").join("state"));
        assert_eq!(win.config_dir(), Path::new("/ad").join("Kiln"));
        assert_eq!(DataEnv { windows: true, ..Default::default() }.default_root().unwrap_err().code, crate::ErrorCode::NotFound);
        assert_eq!(DataEnv::default().default_root().unwrap_err().message, "HOME is not set");
    }
}
