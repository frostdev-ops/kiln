//! Tauri commands: library. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! The content-addressed mod library on kiln_manage::library; imports run as tasks.

use super::TaskId;
use super::app::{spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::library::{self, Detected, LibraryEntry, LibraryRef};
use kiln_manage::lint::{self, LintOptions, LintReport};
use kiln_manage::manifest::ModKind;
use std::path::PathBuf;
use tauri::AppHandle;

#[tauri::command]
pub async fn library_list(kind: Option<ModKind>) -> Result<Vec<LibraryEntry>, Error> {
    blocking(move || library::list(&dirs()?, kind)).await
}

#[tauri::command]
pub async fn library_detect(path: String) -> Result<Detected, Error> {
    blocking(move || library::detect(std::path::Path::new(&path))).await
}

#[tauri::command]
pub async fn library_import(app: AppHandle, paths: Vec<String>) -> Result<TaskId, Error> {
    if paths.is_empty() {
        return Err(Error::invalid("Nothing to import."));
    }
    let title = match paths.as_slice() {
        [one] => format!("Import {}", std::path::Path::new(one).file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_else(|| one.clone())),
        many => format!("Import {} items", many.len()),
    };
    let paths: Vec<PathBuf> = paths.into_iter().map(PathBuf::from).collect();
    Ok(spawn_task(&app, "library_import", title, move |p| to_value(library::import(&dirs()?, &paths, p)?)))
}

#[tauri::command]
pub async fn library_remove(entry: LibraryRef, force: bool) -> Result<(), Error> {
    blocking(move || library::remove(&dirs()?, &entry, force)).await
}

/// `kiln lint` on one library entry (with `profile_id`, the profile's other mods join the cross-mod checks), or on a
/// whole profile (no entry). The report's shape: schema/lint-report.schema.json.
#[tauri::command]
pub async fn library_lint(entry: Option<LibraryRef>, profile_id: Option<String>) -> Result<LintReport, Error> {
    blocking(move || {
        let dirs = dirs()?;
        let options = LintOptions::default();
        match entry {
            Some(e) => lint::lint_library_entry(&dirs, &e, profile_id.as_deref(), &options),
            None => {
                let profile = profile_id.as_deref().ok_or_else(|| Error::invalid("Pick a mod or a profile to check."))?;
                lint::lint_profile(&dirs, profile, &options)
            }
        }
    })
    .await
}
