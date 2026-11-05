//! Extracts the game art the launcher shows (the save editor's icons, UI textures and fonts) from the user's own
//! Enshrouded client, read-only, into the launcher's data folder (`<data>/editor-assets/`, served by the launcher's
//! `kiln-asset` protocol). The art is Keen Games' data: it is never committed, bundled or downloaded; each user makes
//! their own copy from their own install. Docs: docs/kiln/launcher/gameart.md.
//!
//! Pieces: [`kfc`] (KFC4 archive, a port of Kiln's MIT `tools/kfc`), [`refl`] (the exe's reflection records, for field
//! offsets by name), [`res`] (UiTextureResource / UiFontResource), [`texture`] (BCn / RGBA -> RGBA8), [`image`]
//! (resize, tint, lossless WebP), [`work`] (threads, progress, cancel), [`editor`] (what the save editor needs and how
//! each file is made), [`map`] (the admin map's relief, tiles, marker icons and fonts), [`modded`] (items added by Kiln
//! mods, as an overlay for the save editor).
//!
//! Every extraction writes `.source.json` ([`SourceInfo`]): the game build it came from (the `.kfc` version string,
//! Steam build, exe sha256), counts and time. [`status`] compares it with the installed game, so the launcher can
//! offer to re-extract after a game update.

pub mod editor;
pub mod image;
pub mod kfc;
pub mod map;
pub mod modded;
pub mod refl;
pub mod res;
pub mod texture;
pub mod work;

#[cfg(test)]
mod tests;

use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub use editor::EditorOptions;
pub use map::{MapOptions, extract_map};
pub use res::LayoutSource;

/// `<dest>/.source.json`.
pub const SOURCE_FILE: &str = ".source.json";
/// The editor's subfolders this module owns inside its destination (replaced as a whole on each extraction).
pub const EDITOR_DIRS: [&str; 3] = ["fonts", "icons", "ui"];

/// The game build art was made from.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameSource {
    /// The game folder (host path).
    pub path: String,
    /// The `.kfc` version string (`1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54.051725Z`): changes with
    /// every data build, so it identifies the art's game build.
    pub kfc_version: String,
    /// Its first part (`1287369`, Keen's data build).
    pub build: String,
    /// Steam's build id, when known (`26430625`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub steam_build: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exe_sha256: Option<String>,
    /// Where the field offsets came from.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub layouts: Option<LayoutSource>,
}

/// `.source.json`: where the art in a folder came from. Copies from a checkout (`app_assets_import` with a folder, or
/// tools/launcher/fetch-editor-assets.sh, which writes only `{from, copied}`) have `source: "copy"`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceInfo {
    #[serde(default = "one")]
    pub format: u32,
    /// "editor" | "admin"
    #[serde(default)]
    pub kind: String,
    /// "game" (extracted here) | "copy" (copied from a folder)
    #[serde(default = "copy")]
    pub source: String,
    /// The recipe version ([`editor::RECIPE`], [`map::MAP_RECIPE`]) for "game".
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recipe: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub game: Option<GameSource>,
    /// The folder a copy came from.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub from: Option<String>,
    /// Files per group (icons, ui, tinted, fonts).
    #[serde(default)]
    pub counts: BTreeMap<String, u64>,
    /// Resources left out on purpose, by reason.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub skipped: BTreeMap<String, u64>,
    #[serde(default)]
    pub bytes: u64,
    /// RFC 3339 UTC.
    #[serde(default, alias = "copied")]
    pub extracted: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seconds: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool: Option<String>,
    /// Files that could not be made (at most 50 listed).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub problems: Vec<String>,
}

fn one() -> u32 {
    1
}
fn copy() -> String {
    "copy".into()
}

pub fn read_source(dest: &Path) -> Option<SourceInfo> {
    crate::util::read_json::<SourceInfo>(&dest.join(SOURCE_FILE)).ok().flatten()
}

pub fn write_source(dest: &Path, info: &SourceInfo) -> Result<()> {
    crate::util::write_json_atomic(&dest.join(SOURCE_FILE), info)
}

/// What the installed game's data build is, cheaply (the `.kfc` header only).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameBuild {
    pub kfc_version: String,
    pub build: String,
}

pub fn build_of(kfc_version: &str) -> String {
    kfc_version.split('|').next().unwrap_or("").to_string()
}

/// The client's data build (`<game>/enshrouded.kfc` header).
pub fn game_build(game_dir: &Path) -> Result<GameBuild> {
    let k = client_kfc(game_dir)?;
    let v = kfc::read_version(&k)?;
    Ok(GameBuild { build: build_of(&v), kfc_version: v })
}

fn client_kfc(game_dir: &Path) -> Result<PathBuf> {
    let k = game_dir.join("enshrouded.kfc");
    if k.is_file() {
        return Ok(k);
    }
    if game_dir.join("enshrouded_server.kfc").is_file() {
        return Err(Error::new(ErrorCode::Unsupported, "A dedicated server has no UI art. Pick the Enshrouded client."));
    }
    Err(Error::not_found(format!("No enshrouded.kfc in {}.", game_dir.display())))
}

/// The art in a folder compared with the installed game.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtStatus {
    /// "game" | "copy" | None (no `.source.json`)
    pub source: Option<String>,
    /// The data build the art was extracted from (None for copies).
    pub build: Option<String>,
    /// The installed client's data build (None: no client known or unreadable).
    pub game_build: Option<String>,
    /// true: extracted from exactly the installed build with the current recipe; false: the game (or the recipe)
    /// changed since, re-extract; None: unknown (a copy, or no client to compare with).
    pub current: Option<bool>,
    pub extracted: Option<String>,
}

/// The current recipe version of a kind ("editor" | "admin").
pub fn recipe_of(kind: &str) -> u32 {
    if kind == "admin" { map::MAP_RECIPE } else { editor::RECIPE }
}

pub fn status(dest: &Path, game_dir: Option<&Path>) -> ArtStatus {
    let src = read_source(dest);
    let game = game_dir.and_then(|g| game_build(g).ok());
    let current = match (&src, &game) {
        (Some(s), Some(g)) if s.source == "game" => Some(
            s.game.as_ref().is_some_and(|x| x.kfc_version == g.kfc_version)
                && s.recipe == Some(recipe_of(&s.kind)),
        ),
        _ => None,
    };
    ArtStatus {
        source: src.as_ref().map(|s| s.source.clone()),
        build: src.as_ref().and_then(|s| s.game.as_ref().map(|g| g.build.clone())),
        game_build: game.map(|g| g.build),
        current,
        extracted: src.map(|s| s.extracted).filter(|e| !e.is_empty()),
    }
}

/// The opened game data: archive, reflection (when the exe could be read) and the layouts to read it with.
pub struct GameData {
    pub dir: PathBuf,
    pub kfc: kfc::Kfc,
    pub layouts: res::Layouts,
    pub exe_sha256: Option<String>,
    /// Why reflection was not used, if it was not.
    pub reflection_note: Option<String>,
}

impl GameData {
    pub fn open(game_dir: &Path) -> Result<GameData> {
        let kfc = kfc::Kfc::open(&client_kfc(game_dir)?)?;
        let exe = game_dir.join("enshrouded.exe");
        let (types, exe_sha256, note) = match std::fs::read(&exe) {
            Ok(raw) => {
                let sha = crate::hash::sha256_hex(&raw);
                match refl::PeFile::parse(raw).and_then(|pe| refl::TypeDb::from_pe(&pe)) {
                    Ok(t) => (Some(t), Some(sha), None),
                    Err(e) => (None, Some(sha), Some(e.message)),
                }
            }
            Err(e) => (None, None, Some(format!("{}: {e}", exe.display()))),
        };
        let layouts = res::layouts(&kfc, types.as_ref())?;
        Ok(GameData { dir: game_dir.to_path_buf(), kfc, layouts, exe_sha256, reflection_note: note })
    }

    pub fn source(&self, steam_build: Option<String>) -> GameSource {
        GameSource {
            path: self.dir.display().to_string(),
            build: build_of(&self.kfc.version),
            kfc_version: self.kfc.version.clone(),
            steam_build,
            exe_sha256: self.exe_sha256.clone(),
            layouts: Some(self.layouts.source),
        }
    }
}

/// Refuses a destination inside the game folder (Kiln never writes into game folders).
pub(crate) fn refuse_inside_game(dest: &Path, game_dir: &Path) -> Result<()> {
    let canon = |p: &Path| std::fs::canonicalize(p).unwrap_or_else(|_| p.to_path_buf());
    let mut d = dest.to_path_buf();
    // the destination may not exist yet: compare its nearest existing ancestor
    while !d.exists() {
        match d.parent() {
            Some(p) => d = p.to_path_buf(),
            None => break,
        }
    }
    if canon(&d).starts_with(canon(game_dir)) {
        return Err(Error::new(ErrorCode::Refused, "Game art never goes into the game folder.").with_detail(dest.display().to_string()));
    }
    Ok(())
}

pub(crate) fn clean_leftovers(dest: &Path) {
    if let Ok(rd) = std::fs::read_dir(dest) {
        for e in rd.flatten() {
            let n = e.file_name().to_string_lossy().into_owned();
            if n.starts_with(".extract-") || n.starts_with(".old-") {
                let _ = std::fs::remove_dir_all(e.path());
            }
        }
    }
}

/// Moves `staging/<d>` over `dest/<d>` for each folder (the old one is removed after the swap).
pub(crate) fn swap_in(staging: &Path, dest: &Path, dirs: &[&str]) -> Result<()> {
    let stamp = std::process::id();
    for d in dirs {
        let new = staging.join(d);
        let cur = dest.join(d);
        if !new.exists() {
            std::fs::create_dir_all(&new).map_err(crate::util::io_at(&new, "creating"))?;
        }
        let old = dest.join(format!(".old-{d}-{stamp}"));
        if cur.exists() {
            std::fs::rename(&cur, &old).map_err(crate::util::io_at(&cur, "moving"))?;
        }
        if let Err(e) = std::fs::rename(&new, &cur) {
            let _ = std::fs::rename(&old, &cur);
            return Err(crate::util::io_at(&cur, "replacing")(e));
        }
        let _ = std::fs::remove_dir_all(&old);
    }
    Ok(())
}

/// Extracts the save editor's art from the client at `game_dir` into `dest` (`<data>/editor-assets/`): `fonts/`,
/// `icons/`, `ui/` (+ `ui/tinted/`) and `.source.json`. Reads the game read-only; writes into a staging folder and
/// swaps it in at the end, so a canceled or failed run leaves the old art as it was. `steam_build`: Steam's build id
/// for the record.
pub fn extract_editor(
    game_dir: &Path,
    dest: &Path,
    opts: &EditorOptions,
    steam_build: Option<String>,
    p: &mut dyn Progress,
) -> Result<SourceInfo> {
    let t0 = std::time::Instant::now();
    refuse_inside_game(dest, game_dir)?;
    p.report(Some(0.0), "Reading the game's data");
    let g = GameData::open(game_dir)?;
    if p.canceled() {
        return Err(Error::new(ErrorCode::Canceled, "Canceled."));
    }
    let textures = res::ui_textures(&g.kfc, &g.layouts)?;
    let fonts = res::fonts(&g.kfc, &g.layouts)?;
    p.report(Some(0.03), &format!("Found {} UI textures and {} fonts", textures.len(), fonts.len()));
    std::fs::create_dir_all(dest).map_err(crate::util::io_at(dest, "creating"))?;
    clean_leftovers(dest);
    let staging = dest.join(format!(".extract-{}-{:x}", std::process::id(), crate::util::now_unix()));
    let result = (|| {
        let plan = editor::plan(&textures, &fonts, &g.kfc, &staging, opts);
        if plan.jobs.iter().all(|j| !matches!(j, editor::Job::Fit { .. })) {
            return Err(Error::new(ErrorCode::NotFound, "No item icons in this game's data.").with_detail(g.kfc.path.display().to_string()));
        }
        let rep = editor::run_jobs(&g.kfc, &plan.jobs, opts.threads, p, 0.03, 0.95)?;
        if rep.written.get("icons").copied().unwrap_or(0) == 0 {
            return Err(Error::new(ErrorCode::Internal, "No icon could be made.").with_detail(rep.failed.iter().take(10).cloned().collect::<Vec<_>>()));
        }
        p.report(Some(0.99), "Putting the art in place");
        swap_in(&staging, dest, &EDITOR_DIRS)?;
        // guid -> icon name of every item icon (the mod overlay resolves GUID icon aliases with it)
        let index: std::collections::BTreeMap<String, String> = textures
            .iter()
            .filter(|t| t.key.part == 0 && t.name.ends_with("_icon") && editor::safe_name(&t.name))
            .map(|t| (t.key.guid_text(), t.name.trim_end_matches("_icon").to_string()))
            .collect();
        crate::util::write_json_atomic(&dest.join(modded::ICON_INDEX), &index)?;
        let mut problems: Vec<String> = plan.missing.clone();
        problems.extend(rep.failed.iter().cloned());
        problems.truncate(50);
        let mut skipped = plan.skipped.clone();
        if !rep.failed.is_empty() {
            skipped.insert("failed".into(), rep.failed.len() as u64);
        }
        let info = SourceInfo {
            format: 1,
            kind: "editor".into(),
            source: "game".into(),
            recipe: Some(editor::RECIPE),
            game: Some(g.source(steam_build)),
            from: None,
            counts: rep.written.clone(),
            skipped,
            bytes: rep.bytes,
            extracted: crate::util::now_rfc3339(),
            seconds: Some((t0.elapsed().as_secs_f64() * 10.0).round() / 10.0),
            tool: Some(format!("kiln-manage {} gameart", crate::VERSION)),
            problems,
        };
        write_source(dest, &info)?;
        Ok(info)
    })();
    let _ = std::fs::remove_dir_all(&staging);
    result
}
