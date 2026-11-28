//! A removed mod's settings (0.2.0-dev.30, owner 2026-11-20). Remove in the launcher keeps them by default: re-adding
//! the mod brings them back. "Remove its settings too" takes out the settings files Kiln keeps for that mod as well,
//! after a copy into `<data>/backups/mod-settings/<kind>-<id>-<ts>/` (with `removed.json`, which says where each
//! file came from), so it can be undone by hand.
//!
//! Where a mod's settings are, each place only for that kind and id, never another mod's:
//! - every known client game: `kiln/state/placed-settings/<kind>/<id>/` (the settings of a mod Kiln does not place
//!   now: an engine copy's settings, ShroudForge's `extended.mod.json` values, EML's `config.txt`/`config.ini`, SET's
//!   `settings.json`), and the settings files (by extension, [`crate::settings_files`]) in
//!   `kiln/kept-mod-files/<kind>/<id>/` (other files the mod wrote stay);
//! - every config folder: each profile's `<data>/profiles/<pid>/config/`, a copied profile's
//!   `<game>/kiln/profiles/<pid>/config/`, the game's own `kiln/config/`. There: Kiln's config files of the mod
//!   (for the kinds that use them: `<id>.json`, `<id>-server.json`, `<id>-client.json` and every file a snapshot in
//!   `.schema/` names as the mod's, with those snapshots), an engine's values file and schema (`<kind>/<id>.<ext>`,
//!   `.schema/<kind>/<id>.json`: EMF's `emf/<id>.ini`), and Shroudtopia's entry `mods.<key>` in the shared
//!   `shroudtopia/shroudtopia.json` (only that entry).
//!
//! Left alone: Kiln's own config files (`native.json`, `keybinds.json`, ...), a bare `<id>.json` while a mod of
//! another kind has the same id, a folder of the player's own (a ShroudForge folder Kiln did not place), a copy still
//! placed in a game (a profile active there lists the mod: its settings reach the stash at that game's next placement),
//! servers, world data (`kiln/data/worlds`) and native.json's switches. The settings copies of kept versions
//! (`<library>/kiln-kept`) go with the versions when they leave the Library.

use crate::engine_mods::refuse_symlink_path;
use crate::manifest::ModKind;
use crate::paths::DataDirs;
use crate::util::io_at;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// `<data>/backups/mod-settings/`: one folder per removal.
pub const BACKUP_DIR: &str = "mod-settings";
/// The record in each backup folder.
pub const BACKUP_RECORD: &str = "removed.json";
/// Kiln's own files in a config folder: never a mod's, whatever its id.
pub(crate) const KILN_OWN: [&str; 13] = ["native", "kiln", "network", "chat", "permissions", "keybinds", "rules", "compat-rules", "tk-boot",
    "tk-config", "menus-client", "menus-server", "config-sync"];
/// The kinds whose mods write Kiln's config files (`<configDir>/<id>.json`): Kiln.Api, native mods through
/// KILN_PATH_CONFIG, plugins, and Shroudtopia's host config API.
const KILN_CONFIG_KINDS: [ModKind; 4] = [ModKind::Kiln, ModKind::Native, ModKind::Plugin, ModKind::Shroudtopia];

/// What a settings removal did.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsRemoval {
    /// What went (host paths): a folder, a file, or `<file> (mods.<key>)` for an entry of a shared file.
    pub removed: Vec<String>,
    /// The copy in the launcher's backups folder; None when there was nothing to remove.
    pub backup: Option<String>,
    /// What was left on purpose or could not go, one sentence each.
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, PartialEq)]
enum What {
    Dir,
    File,
    /// `mods.<key>` of a shared JSON file.
    Entry(String),
}

#[derive(Debug, Clone)]
struct Item {
    path: PathBuf,
    /// Links are refused from here down.
    root: PathBuf,
    what: What,
    /// Its place in the backup folder.
    backup: PathBuf,
}

impl Item {
    fn label(&self) -> String {
        match &self.what {
            What::Entry(key) => format!("{} (mods.{key})", self.path.display()),
            _ => self.path.display().to_string(),
        }
    }
}

/// Removes the settings Kiln keeps for `kind`/`id` (module docs), after a copy into the backups folder. `keys`: more
/// names the mod's entry may have in a shared file (a Shroudtopia mod's `mod.json` name). Refused (Conflict) while the
/// Library still holds a version of the mod, and (GameRunning) while a known game runs.
pub fn remove(dirs: &DataDirs, kind: ModKind, id: &str, keys: &[String]) -> Result<SettingsRemoval> {
    let mut finder = crate::proc::Finder::default();
    remove_with(dirs, kind, id, keys, &mut |dir, exe| finder.find(dir, exe))
}

/// [`remove`] with the process lookup given (`find(game folder, exe name)`), for tests.
pub fn remove_with(
    dirs: &DataDirs,
    kind: ModKind,
    id: &str,
    keys: &[String],
    find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>,
) -> Result<SettingsRemoval> {
    if !crate::manifest::is_mod_id(id) || id.contains(['/', '\\']) || id.starts_with('.') {
        return Err(Error::invalid(format!("not a mod id: {id}")));
    }
    if crate::library::list(dirs, Some(kind))?.iter().any(|e| e.id == id && !e.versions.is_empty()) {
        return Err(Error::new(ErrorCode::Conflict, format!("{} {id} is still in the Library: its settings stay", kind.as_str())));
    }
    let games = client_games(dirs)?;
    for g in &games {
        if crate::profile::running_process_with(&g.path, &g.exe, &mut *find)?.is_some() {
            return Err(Error::new(ErrorCode::GameRunning, "Close Enshrouded first: the game may be reading the mod's settings."));
        }
    }
    let mut report = SettingsRemoval::default();
    let items = plan(dirs, &games, kind, id, keys, &mut report.notes)?;
    if items.is_empty() {
        return Ok(report);
    }
    let backup = backup(dirs, kind, id, &items)?;
    report.backup = Some(backup.display().to_string());
    for item in &items {
        match take_out(item) {
            Ok(true) => report.removed.push(item.label()),
            Ok(false) => {}
            Err(e) => report.notes.push(format!("{} stays: {} (a copy is in {})", item.label(), e.message, backup.display())),
        }
    }
    Ok(report)
}

/// The Configs window's "Clear settings" (0.2.0-dev.31.30): takes the config files of mods that are not installed out of
/// a profile's config folder (`files`, as `config_list` names them: each with its schema snapshot), after a copy into
/// `<data>/backups/mod-settings/config-<profile>-<time>/` (the folder Remove's "its settings too" uses, laid out the same
/// way: `profiles/<pid>/config/...`, and `removed.json`).
///
/// Refused as a whole, with nothing touched: a name that leaves the config folder (Invalid), a profile that is not there
/// (NotFound), a file that is not there (NotFound), a file that is not an orphan right now (Conflict: its mod is
/// installed, in the profile on or off, in the Library or in a game folder, or the file is Kiln's own, or nobody's we
/// know), a place the launcher could not read to tell (Conflict), and a running game (GameRunning). So an installed mod's
/// config is never touched, whatever the caller sends. The answer is [`SettingsRemoval`]: what went, where the copy is.
pub fn clear_orphans(dirs: &DataDirs, profile_id: &str, files: &[String]) -> Result<SettingsRemoval> {
    let mut finder = crate::proc::Finder::default();
    clear_orphans_with(dirs, profile_id, files, &mut |dir, exe| finder.find(dir, exe))
}

/// [`clear_orphans`] with the process lookup given (`find(game folder, exe name)`), for tests.
pub fn clear_orphans_with(
    dirs: &DataDirs,
    profile_id: &str,
    files: &[String],
    find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>,
) -> Result<SettingsRemoval> {
    let mut want: Vec<&str> = Vec::new();
    for f in files {
        crate::config::check_file_name(f)?;
        if !want.contains(&f.as_str()) {
            want.push(f);
        }
    }
    if want.is_empty() {
        return Ok(SettingsRemoval::default());
    }
    crate::profile::get(dirs, profile_id)?;
    for g in &client_games(dirs)? {
        if crate::profile::running_process_with(&g.path, &g.exe, &mut *find)?.is_some() {
            return Err(Error::new(ErrorCode::GameRunning, "Close Enshrouded first: the game may be reading the settings."));
        }
    }
    let cfg = dirs.profile(profile_id).join("config");
    let list = if cfg.is_dir() { crate::config::list(&cfg)? } else { Vec::new() };
    let gone = |f: &str| Error::new(ErrorCode::Conflict, format!("{f} is not the settings of a mod that is gone: it stays."));
    // each file's mod (Kiln's own files and a file whose mod is not known have none: they stay), then whether those mods
    // are installed anywhere, read now (`Installed::read_for`: the places only while one of them is missing)
    let mut owners: Vec<(&str, String)> = Vec::new();
    for f in &want {
        let Some(info) = list.iter().find(|i| i.file == *f) else {
            return Err(Error::not_found(format!("No config {f} in this profile.")));
        };
        owners.push((*f, crate::config_orphans::owner_of(info).ok_or_else(|| gone(f))?));
    }
    let mut mods: Vec<String> = Vec::new();
    for (_, id) in &owners {
        if !mods.iter().any(|m| m.eq_ignore_ascii_case(id)) {
            mods.push(id.clone());
        }
    }
    let installed = crate::config_orphans::Installed::read_for(dirs, profile_id, &mods);
    if let Some(why) = installed.incomplete() {
        return Err(Error::new(ErrorCode::Conflict, format!("Kiln cannot tell which mods are installed ({why}): nothing was cleared.")));
    }
    if let Some((f, _)) = owners.iter().find(|(_, id)| installed.has(id)) {
        return Err(gone(f));
    }
    let base = PathBuf::from("profiles").join(tag(profile_id)).join("config");
    // each file, then its snapshot (`groups`: their places in `items`)
    let mut items = Vec::new();
    let mut groups = Vec::new();
    for f in &want {
        let snapshot = format!("{}/{}", crate::config::SCHEMA_DIR, Path::new(f).with_extension("json").to_string_lossy().replace('\\', "/"));
        let from = items.len();
        for rel in [f.to_string(), snapshot] {
            let path = cfg.join(&rel);
            if std::fs::symlink_metadata(&path).is_ok_and(|m| m.is_file()) {
                items.push(Item { path, root: dirs.root.clone(), what: What::File, backup: base.join(&rel) });
            }
        }
        groups.push(from..items.len());
    }
    let mut report = SettingsRemoval::default();
    if items.is_empty() {
        return Ok(report);
    }
    let folder = format!("config-{}", tag(profile_id));
    let backup = backup_as(dirs, &folder, "config", profile_id, &mods, &items)?;
    report.backup = Some(backup.display().to_string());
    for group in groups {
        // a file that could not go keeps its snapshot (review of 0.2.0-dev.31.31): without it the file would be nobody's,
        // listed with the installed files and no longer clearable; with it, it stays marked and Clear can run again
        let mut stuck = false;
        for item in &items[group] {
            if stuck {
                report.notes.push(format!("{} stays with its file (a copy is in {})", item.label(), backup.display()));
                continue;
            }
            match take_out(item) {
                Ok(true) => report.removed.push(item.label()),
                Ok(false) => {}
                Err(e) => {
                    stuck = true;
                    report.notes.push(format!("{} stays: {} (a copy is in {})", item.label(), e.message, backup.display()));
                }
            }
        }
    }
    Ok(report)
}

/// The client games the launcher knows, each folder once.
fn client_games(dirs: &DataDirs) -> Result<Vec<crate::game::GameInstall>> {
    let mut out: Vec<crate::game::GameInstall> = Vec::new();
    for g in crate::game::known(dirs)?.into_iter().filter(|g| g.kind == crate::game::GameKind::Client) {
        let c = crate::steam::canon(&g.path);
        if !out.iter().any(|d| crate::steam::same_path(&crate::steam::canon(&d.path), &c)) {
            out.push(g);
        }
    }
    Ok(out)
}

/// A folder name from an id (games, profiles): letters, digits, `.`, `_`, `-`; anything else `_`.
fn tag(s: &str) -> String {
    let t: String = s.chars().map(|c| if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') { c } else { '_' }).collect();
    if t.is_empty() || t.starts_with('.') { format!("_{t}") } else { t }
}

fn sub_dirs(dir: &Path) -> Vec<(String, PathBuf)> {
    let mut out: Vec<(String, PathBuf)> = std::fs::read_dir(dir).into_iter().flatten().flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_dir()))
        .filter_map(|e| e.file_name().to_str().map(|n| (n.to_string(), e.path())))
        .collect();
    out.sort();
    out
}

/// Every place the mod's settings are now ([`remove`]).
fn plan(dirs: &DataDirs, games: &[crate::game::GameInstall], kind: ModKind, id: &str, keys: &[String], notes: &mut Vec<String>)
    -> Result<Vec<Item>> {
    let mut items = Vec::new();
    // Kiln's config files go by the bare id: only for the kinds that write them, and not while a mod of another kind has
    // the same id (its file, then)
    let mut bare = KILN_CONFIG_KINDS.contains(&kind);
    if bare && let Some(other) = same_id_elsewhere(dirs, kind, id) {
        bare = false;
        notes.push(format!("{id}.json and the other config files named after {id} stay: a {} mod has the same id", other.as_str()));
    }
    let mut keys: Vec<String> = std::iter::once(id.to_string()).chain(keys.iter().cloned()).collect();
    keys.dedup();
    let cfg = Cfg { kind, id, bare, keys: &keys };
    for g in games {
        let base = PathBuf::from("games").join(tag(&g.id));
        let stash = g.path.join("kiln/state/placed-settings").join(kind.as_str()).join(id);
        if stash.is_dir() {
            items.push(Item { path: stash, root: g.path.clone(), what: What::Dir,
                backup: base.join("kiln/state/placed-settings").join(kind.as_str()).join(id) });
        }
        let kept = g.path.join(crate::engine_mods::KEPT_MOD_FILES).join(kind.as_str()).join(id);
        for rel in files_under(&kept) {
            if crate::settings_files::is_settings_extension(&rel) {
                items.push(Item { path: kept.join(&rel), root: g.path.clone(), what: What::File,
                    backup: base.join(crate::engine_mods::KEPT_MOD_FILES).join(kind.as_str()).join(id).join(&rel) });
            }
        }
        cfg.items(&g.path.join(crate::paths::game::CONFIG), &g.path, &base.join("kiln/config"), &mut items);
        for (pid, p) in sub_dirs(&g.path.join(crate::paths::game::COPY_PROFILES)) {
            cfg.items(&p.join("config"), &g.path, &base.join("kiln/profiles").join(tag(&pid)).join("config"), &mut items);
        }
    }
    for (pid, p) in sub_dirs(&dirs.profiles()) {
        cfg.items(&p.join("config"), &dirs.root, &PathBuf::from("profiles").join(tag(&pid)).join("config"), &mut items);
    }
    Ok(items)
}

/// Another kind that has a mod with this id, in the Library or in a profile.
fn same_id_elsewhere(dirs: &DataDirs, kind: ModKind, id: &str) -> Option<ModKind> {
    if let Some(e) = crate::library::list(dirs, None).unwrap_or_default().into_iter().find(|e| e.kind != kind && e.id == id) {
        return Some(e.kind);
    }
    for s in crate::profile::list(dirs).unwrap_or_default() {
        let Ok(p) = crate::profile::get(dirs, &s.id) else { continue };
        if let Some(k) = ModKind::ALL.into_iter().find(|k| *k != kind && p.mods.list(*k).iter().any(|m| m.id == id)) {
            return Some(k);
        }
    }
    None
}

/// The files under `dir` (relative, with `/`), sorted; links are not followed.
fn files_under(dir: &Path) -> Vec<String> {
    fn walk(dir: &Path, rel: &str, out: &mut Vec<String>) {
        for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
            let Ok(ft) = e.file_type() else { continue };
            let Some(name) = e.file_name().to_str().map(str::to_owned) else { continue };
            let r = if rel.is_empty() { name } else { format!("{rel}/{name}") };
            if ft.is_dir() { walk(&e.path(), &r, out) } else if ft.is_file() { out.push(r) }
        }
    }
    let mut out = Vec::new();
    if std::fs::symlink_metadata(dir).is_ok_and(|m| m.is_dir()) { walk(dir, "", &mut out); }
    out.sort();
    out
}

/// What a config folder holds of one mod.
struct Cfg<'a> {
    kind: ModKind,
    id: &'a str,
    bare: bool,
    keys: &'a [String],
}

impl Cfg<'_> {
    fn items(&self, cfg: &Path, root: &Path, backup: &Path, items: &mut Vec<Item>) {
        if !std::fs::symlink_metadata(cfg).is_ok_and(|m| m.is_dir()) { return; }
        let mut file = |rel: &str| {
            let p = cfg.join(rel);
            if std::fs::symlink_metadata(&p).is_ok_and(|m| m.is_file()) && !items.iter().any(|i: &Item| i.path == p) {
                items.push(Item { path: p, root: root.to_path_buf(), what: What::File, backup: backup.join(rel) });
            }
        };
        if self.bare {
            let schema = cfg.join(crate::config::SCHEMA_DIR);
            let mut names: Vec<String> = ["", "-server", "-client"].iter().map(|s| format!("{}{s}.json", self.id)).collect();
            // Register<T>(kind, name) files: the snapshot says whose they are (`mod`)
            for e in std::fs::read_dir(&schema).into_iter().flatten().flatten() {
                let Some(n) = e.file_name().to_str().map(str::to_owned) else { continue };
                if !n.ends_with(".json") || !e.file_type().is_ok_and(|t| t.is_file()) { continue; }
                let snap: Option<serde_json::Value> = std::fs::read_to_string(e.path()).ok().and_then(|t| serde_json::from_str(&t).ok());
                let owner = snap.as_ref().and_then(|s| s.get("mod")).and_then(|m| m.as_str());
                let named = snap.as_ref().and_then(|s| s.get("file")).and_then(|f| f.as_str());
                if owner == Some(self.id) && named == Some(n.as_str()) && !names.contains(&n) { names.push(n); }
            }
            for n in names {
                let stem = n.trim_end_matches(".json");
                if KILN_OWN.contains(&stem) { continue; }
                // a snapshot that names another mod: that file is the other mod's
                let snap = schema.join(&n);
                let owner = std::fs::read_to_string(&snap).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
                    .and_then(|s| s.get("mod").and_then(|m| m.as_str()).map(str::to_owned));
                if owner.as_deref().is_some_and(|o| o != self.id) { continue; }
                file(&n);
                file(&format!("{}/{n}", crate::config::SCHEMA_DIR));
            }
        }
        // an engine's values file (`emf/<id>.ini`) and its schema snapshot
        let k = self.kind.as_str();
        for e in std::fs::read_dir(cfg.join(k)).into_iter().flatten().flatten() {
            let Some(n) = e.file_name().to_str().map(str::to_owned) else { continue };
            let stem = n.rsplit_once('.').map(|(s, _)| s).unwrap_or(&n);
            if stem == self.id && crate::settings_files::is_settings_extension(&n) && !(self.kind == ModKind::Shroudtopia && n == "shroudtopia.json") {
                file(&format!("{k}/{n}"));
            }
        }
        file(&format!("{}/{k}/{}.json", crate::config::SCHEMA_DIR, self.id));
        if self.kind == ModKind::Shroudtopia {
            let shared = crate::engine_mods::shroudtopia_file(cfg);
            let doc = std::fs::read_to_string(&shared).ok().and_then(|t| crate::jsonc::parse(t.trim_start_matches('\u{feff}')).ok());
            let mods = doc.as_ref().and_then(|d| d.get("mods")).and_then(|m| m.as_object());
            for key in self.keys {
                if mods.is_some_and(|m| m.contains_key(key)) {
                    items.push(Item { path: shared.clone(), root: root.to_path_buf(), what: What::Entry(key.clone()),
                        backup: backup.join("shroudtopia/shroudtopia.json") });
                }
            }
        }
    }
}

/// Copies every item into a new backup folder and writes its record, durably, before anything is removed.
fn backup(dirs: &DataDirs, kind: ModKind, id: &str, items: &[Item]) -> Result<PathBuf> {
    backup_as(dirs, &format!("{}-{}", kind.as_str(), tag(id)), kind.as_str(), id, &[], items)
}

/// [`backup`] in `<data>/backups/mod-settings/<name>-<time>/`; `kind` and `id` are what the record says was removed (a
/// mod's, or `config` and a profile's id for [`clear_orphans`], which also names the `mods` it belongs to).
fn backup_as(dirs: &DataDirs, name: &str, kind: &str, id: &str, mods: &[String], items: &[Item]) -> Result<PathBuf> {
    let base = dirs.backups().join(BACKUP_DIR).join(format!("{name}-{}", crate::util::ts_compact(crate::util::now_unix())));
    let to = (1..).map(|n| if n == 1 { base.clone() } else { crate::util::with_suffix(&base, &format!("-{n}")) })
        .find(|p| std::fs::symlink_metadata(p).is_err()).expect("a free name");
    std::fs::create_dir_all(&to).map_err(io_at(&to, "creating"))?;
    let mut record = Vec::new();
    for item in items {
        refuse_symlink_path(&item.root, &item.path)?;
        let dst = to.join(&item.backup);
        match item.what {
            What::Dir => { crate::util::copy_tree(&item.path, &dst)?; }
            What::File | What::Entry(_) => {
                if let Some(p) = dst.parent() { std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?; }
                std::fs::copy(&item.path, &dst).map_err(io_at(&dst, "copying to"))?;
            }
        }
        let mut r = serde_json::json!({ "from": item.path.display().to_string(), "backup": item.backup.to_string_lossy().replace('\\', "/") });
        if let What::Entry(key) = &item.what { r["entry"] = serde_json::json!(format!("mods.{key}")); }
        record.push(r);
    }
    let mut doc = serde_json::json!({ "format": 1, "kind": kind, "id": id, "removed": crate::util::now_rfc3339(), "items": record });
    if !mods.is_empty() {
        doc["mods"] = serde_json::json!(mods);
    }
    let text = serde_json::to_string_pretty(&doc)?;
    std::fs::write(to.join(BACKUP_RECORD), text + "\n").map_err(io_at(&to, "writing the record in"))?;
    crate::durable::flush_contents(&to)?;
    Ok(to)
}

/// Removes one item (its copy is in the backup). false: it was gone already.
fn take_out(item: &Item) -> Result<bool> {
    refuse_symlink_path(&item.root, &item.path)?;
    match &item.what {
        What::Dir => {
            if !item.path.is_dir() { return Ok(false); }
            std::fs::remove_dir_all(&item.path).map_err(io_at(&item.path, "removing"))?;
        }
        What::File => {
            if !item.path.is_file() { return Ok(false); }
            std::fs::remove_file(&item.path).map_err(io_at(&item.path, "removing"))?;
        }
        What::Entry(key) => {
            let text = std::fs::read_to_string(&item.path).map_err(io_at(&item.path, "reading"))?;
            let path = [crate::jsonc::PathSeg::Key("mods".into()), crate::jsonc::PathSeg::Key(key.clone())];
            let out = crate::jsonc::remove_value(&text, &path)?;
            if out == text { return Ok(false); }
            crate::durable::replace(&item.path, out.as_bytes())?;
            return Ok(true);
        }
    }
    // the folders it leaves empty, up to the one that holds every mod of the kind (`placed-settings/<kind>`,
    // `kept-mod-files/<kind>`) or the config folder's own (`emf`, `.schema/emf`); never past `kiln/` or `config/`
    let mut up = item.path.parent();
    while let Some(d) = up {
        let name = d.file_name().and_then(|n| n.to_str()).unwrap_or("");
        if matches!(name, "config" | "kiln" | "state" | "placed-settings" | "kept-mod-files" | ".schema") || ModKind::parse(name).is_some()
            || !d.starts_with(&item.root) || std::fs::remove_dir(d).is_err() {
            break;
        }
        up = d.parent();
    }
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folder_tags_keep_safe_names_only() {
        assert_eq!(tag("g-client"), "g-client");
        assert_eq!(tag("server:/x/y"), "server__x_y");
        assert_eq!(tag(".."), "_..");
        assert_eq!(tag(""), "_");
    }
}
