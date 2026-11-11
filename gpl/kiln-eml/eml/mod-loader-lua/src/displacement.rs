// SPDX-License-Identifier: GPL-3.0-or-later
// Lossless collision handling. Neither journals nor saved containers in this folder are ever pruned.
use std::{collections::BTreeMap, fs, io, path::{Path, PathBuf}};
use crate::{ownership::{checked_path, digest, Digest}, transaction};

thread_local! { static MOVED: std::cell::RefCell<BTreeMap<PathBuf, Vec<String>>> = const { std::cell::RefCell::new(BTreeMap::new()) }; }
pub(crate) fn reset() { MOVED.with(|m| m.borrow_mut().clear()); }

pub(crate) struct Plan { folder: PathBuf, files: BTreeMap<String, Digest> }
impl Plan {
    pub(crate) fn record(root: &Path, stem: &str, vanilla: usize, files: BTreeMap<String, Digest>) -> io::Result<Self> {
        let parent = root.join("kiln/eml/displaced");
        checked_path(root, &parent)?;
        fs::create_dir_all(&parent)?;
        let folder = parent.join(uuid::Uuid::new_v4().to_string());
        fs::create_dir(&folder)?; // a new private namespace, never an existing recovery folder
        for dir in [root, &root.join("kiln"), &root.join("kiln/eml"), &parent] { transaction::sync_dir(dir)?; }
        let journal = serde_json::json!({"format":1,"stem":stem,"vanillaContainers":vanilla,"files":files});
        transaction::atomic_write(&folder.join("moves.json"), &serde_json::to_vec(&journal)?)?;
        Ok(Self { folder, files })
    }

    /// Only after the durable vanilla-header barrier, under the same writer guard as the ownership check.
    /// All moves precede any further archive writes. A failed move leaves earlier moves recoverable, never undone.
    pub(crate) fn apply(&self, root: &Path, mut boundary: impl FnMut() -> io::Result<()>) -> io::Result<()> {
        checked_path(root, &self.folder)?;
        for (name, expected) in &self.files {
            let source = root.join(name);
            if digest(root, &source)? != *expected { return Err(io::Error::other(format!("container {name} changed since its recovery move was recorded; retained without publishing"))); }
            let target = self.folder.join(name);
            transaction::move_new(&source, &target).map_err(|e| io::Error::other(format!("cannot move {name} into {}: {e}; no further archives published", self.folder.display())))?;
            MOVED.with(|m| m.borrow_mut().entry(self.folder.clone()).or_default().push(format!("{name} ({} bytes) -> {}", expected.size, target.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/"))));
            boundary()?;
        }
        Ok(())
    }
}

pub(crate) fn guidance(root: &Path) -> io::Result<Vec<String>> {
    let parent = root.join("kiln/eml/displaced");
    checked_path(root, &parent)?;
    let entries = match fs::read_dir(&parent) {
        Ok(entries) => entries,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(e) => return Err(e),
    };
    let mut messages = Vec::new();
    for e in entries {
        let e = e?;
        checked_path(root, &e.path())?;
        if !e.file_type()?.is_dir() { continue; }
        let moved = MOVED.with(|m| m.borrow().get(&e.path()).cloned());
        messages.push(match moved {
            Some(files) => format!("Archive containers moved aside: {}. The originals are preserved; Kiln never deletes this recovery folder", files.join(", ")),
            None => format!("Archive recovery folder retained: {}/. Inspect its move journal and any saved containers; Kiln never deletes this folder", e.path().strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/")),
        });
    }
    messages.sort();
    Ok(messages)
}
