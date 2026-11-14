//! Update checks for installed mods: is a newer version published, and where. A check ends in a notice and a link.
//! Nothing here downloads a mod, installs one, changes a profile or reloads anything, and nothing in the game makes
//! a request: only the launcher and `kiln updates check` call this module.
//!
//! | module | what |
//! |---|---|
//! | [`status`] | [`ModUpdateStatus`]: the one result per installed mod that the CLI prints and the launcher shows |
//! | [`receipt`] | source receipts (`<data>/updates/receipts.json`): what Kiln saw when a mod was installed or imported, the source accepted for a mod, manual mappings, the migration of Nexus install records |
//! | [`coordinator`] | which source a mod has (manual mapping, then receipt, then manifest), the confirmation a changed source needs, the fan-out to providers, one result set |
//! | [`github_select`] | which release of a GitHub repository is a mod's newest version and how it compares with the installed one: pure functions, no request |
//! | [`github`] | the GitHub provider: anonymous reads of a repository's releases, at most one small metadata file per check, never a mod's zip |
//! | [`cache`] | what checks remember (`<data>/updates/cache.json`): the last answer per source, its validators, waits after failures and request limits |
//! | [`nexus`] | the Nexus Mods provider: the existing signed-in check of the catalog, unchanged, as statuses |
//!
//! The rule every part keeps: a check that did not succeed never reads as "up to date". It is `unknown` with a
//! reason, or an earlier "update available" kept and marked stale.

pub mod cache;
pub mod coordinator;
pub mod github;
pub mod github_select;
pub mod nexus;
pub mod receipt;
pub mod status;

pub use coordinator::{check, CheckItem, CheckOptions, CheckReport, Target, UpdateProvider};
pub use receipt::{SourceIdentity, SourceReceipt};
pub use status::{CandidateIdentity, Compatibility, Coverage, ModUpdateStatus, Provider, SourceInfo, SourceOrigin, UnknownReason, UpdateState};

use crate::paths::DataDirs;
use crate::Result;
use std::path::PathBuf;

/// The version of the rules a receipt or a cached answer was made under (source selection, release selection,
/// comparison). Raised when a rule changes what an old record means; older records are then read as unbound.
pub const POLICY_VERSION: u32 = 2;

/// An answer older than this is stale: shown, marked, and never as "up to date". Also how often an automatic check
/// asks a provider about one source.
pub const STALE_AFTER_SECS: i64 = 24 * 60 * 60;

/// `<data>/updates`
pub fn root_of(dirs: &DataDirs) -> PathBuf {
    dirs.root.join("updates")
}

/// Serializes a read-modify-write of this folder's files across threads and processes (the launcher and the CLI).
pub(crate) fn lock(dirs: &DataDirs) -> Result<std::fs::File> {
    let root = root_of(dirs);
    std::fs::create_dir_all(&root).map_err(crate::util::io_at(&root, "creating"))?;
    let path = root.join(".lock");
    let f = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(&path).map_err(crate::util::io_at(&path, "opening"))?;
    f.lock().map_err(crate::util::io_at(&path, "locking"))?;
    Ok(f)
}
