// SPDX-License-Identifier: MIT
//! The launcher's data folder lock (`kiln_manage::data_folder::DataLock`, contracts.md section 3), for `kilnctl`.
//! A launcher holds it exclusively while it moves the data folder or removes an unfinished copy, and those removals
//! prove a file by comparing it with the other folder: a writer that ignored the lock could make them delete the wrong
//! thing. So every `kilnctl` command that uses the data root holds the lock shared for as long as it uses the root
//! ([`Use`], decided once per command in `main`). Shared holders coexist: a launcher, the `kiln` CLI and a `kilnctl`
//! started by either (or by `kilnctl start`) never block each other.
use anyhow::{Result, bail};
use kiln_manage::data_folder::DataLock;
use kiln_manage::paths::DataDirs;

/// What a command does with the data root, and so how long it holds the lock.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Use {
    /// It reads or writes the data root: the lock for the whole command.
    Whole,
    /// It never touches the data root (instance files, the server folder, the Wine prefix, Kiln's state folder):
    /// no lock, so a long run (a server in the foreground, a log followed, a SteamCMD update) never blocks a move.
    None,
    /// Only one step of it uses the data root: that step takes the lock itself ([`hold`]).
    Part,
}

/// The sentence a command fails with while a launcher holds the folder exclusively.
pub const BUSY: &str = "Kiln is moving or recovering its data folder: wait for it to finish.";

/// The lock, held while this value lives (None: no data folder, so nothing to lock).
pub struct Held(#[allow(dead_code)] Option<DataLock>);

/// Takes the data folder's lock shared. Fails with [`BUSY`] before anything is written when a launcher holds it
/// exclusively. No data folder yet (a first run) or a pointer that cannot be read: no lock and no folder is made; the
/// command's own use of the root reports a broken pointer as before. A lock file that cannot be made (a read-only
/// folder): no lock, as a launcher does, with a note.
pub fn hold() -> Result<Held> {
    let Ok(dirs) = DataDirs::locate() else { return Ok(Held(None)) };
    if !dirs.root.is_dir() {
        return Ok(Held(None));
    }
    match DataLock::shared(&dirs.root) {
        Ok(Some(l)) => Ok(Held(Some(l))),
        Ok(None) => bail!("{BUSY}"),
        Err(e) => {
            eprintln!("kilnctl: note: the data folder's lock cannot be taken ({}); going on without it", e.message);
            Ok(Held(None))
        }
    }
}
