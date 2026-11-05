//! Where things live. Launcher data: `~/.local/share/kiln/` (`$XDG_DATA_HOME/kiln`), Windows `%LOCALAPPDATA%\Kiln\`,
//! `$KILN_DATA_DIR` overrides both. Layout (contracts.md, "Launcher data"):
//! `library/{kiln,native,plugin,emf}/<id>/<version>+<hash8>/`, `profiles/<pid>/{meta.json,config/,data/,save-backups/}`,
//! `installs/<installId>.json`, `runtime/`, `schema-cache/`, `backups/`, `logs/`, `settings.json`.

use crate::manifest::ModKind;
use crate::{Error, Result};
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

    /// `$KILN_DATA_DIR`, else the platform default.
    pub fn locate() -> Result<Self> {
        if let Some(d) = std::env::var_os("KILN_DATA_DIR").filter(|d| !d.is_empty()) {
            return Ok(DataDirs::at(d));
        }
        #[cfg(windows)]
        {
            let base = std::env::var_os("LOCALAPPDATA").ok_or_else(|| Error::not_found("LOCALAPPDATA is not set"))?;
            Ok(DataDirs::at(Path::new(&base).join("Kiln")))
        }
        #[cfg(not(windows))]
        {
            if let Some(x) = std::env::var_os("XDG_DATA_HOME").filter(|d| !d.is_empty()) {
                return Ok(DataDirs::at(Path::new(&x).join("kiln")));
            }
            let home = std::env::var_os("HOME").ok_or_else(|| Error::not_found("HOME is not set"))?;
            Ok(DataDirs::at(Path::new(&home).join(".local/share/kiln")))
        }
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
