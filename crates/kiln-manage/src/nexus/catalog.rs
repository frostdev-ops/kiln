//! The online catalog: Enshrouded's mods on Nexus Mods, which of them are Kiln mods, a mod's details, and the mods
//! installed from it with their updates.
//!
//! **The rule for a Kiln mod** (docs/kiln/launcher/catalog-authors.md): one of the mod's active files (main, update,
//! optional, miscellaneous; the three most recently updated are checked) has, in its newest version's archive, a
//! `kiln.mod.json` (at the root of the zip or in a folder). Kiln reads that from Nexus's archive manifest (the file
//! list Nexus publishes for each upload), so nothing is downloaded to find out. The same look classifies other mods
//! Kiln can load: a `native.json` (native mod), an EMF `mod.json` next to a DLL, a bare DLL. Only mods created or
//! updated after [`KILN_EPOCH`] can be Kiln mods (Kiln did not exist before), so older mods are never checked.
//!
//! The answers are kept in `<data>/nexus/verdicts.json` per mod and its `updated_at`: a mod is looked at again only
//! after its page changes. The mod list itself is kept 10 minutes in memory.
//!
//! Files and their manifests need the user's API key (anonymous calls get search and mod pages only): without one the
//! catalog lists every Enshrouded mod and says that finding Kiln mods needs the key.

use super::api::{Client, FileVersion, ModDetails, ModFile, ModSummary};
use super::version;
use crate::library::LibraryRef;
use crate::paths::DataDirs;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Enshrouded's domain on Nexus Mods.
pub const GAME: &str = "enshrouded";
/// The Kiln page (its game-scoped id).
pub const KILN_MOD: &str = "301";
/// Kiln mods exist only after this (mods not updated since are never checked).
pub const KILN_EPOCH: &str = "2026-11-01T00:00:00Z";
pub const SITE: &str = "https://www.nexusmods.com/enshrouded/mods/";
const LIST_TTL: Duration = Duration::from_secs(600);
const LATEST_TTL_SECS: i64 = 1800;
/// Mods checked per search call; the answer says how many wait (the UI asks again).
pub const CHECK_BATCH: usize = 12;
const FILES_CHECKED: usize = 3;
const MAX_LIST_PAGES: usize = 30;

pub fn mod_url(gsid: &str) -> String {
    format!("{SITE}{gsid}")
}

/// The files tab with one file picked; `nmm=1` shows its "Mod Manager Download" (an nxm link for this launcher).
pub fn files_url(gsid: &str, file_gsid: Option<&str>) -> String {
    match file_gsid {
        Some(f) => format!("{SITE}{gsid}?tab=files&file_id={f}&nmm=1"),
        None => format!("{SITE}{gsid}?tab=files"),
    }
}

/// What a mod's archive holds, as far as Kiln loads it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ModType {
    /// `kiln.mod.json`: a Kiln mod (the catalog's own).
    Kiln,
    /// `native.json`
    Native,
    /// EMF's `mod.json` next to a DLL
    Emf,
    /// a DLL without a manifest (Kiln wraps it as a native mod)
    Dll,
    /// `kiln-modpack.json`
    Modpack,
    /// nothing Kiln loads (pak files, textures, tools, saves)
    Other,
}

/// What the catalog found for one mod (cached per `updated`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Verdict {
    pub kind: ModType,
    /// The mod's `updated_at` this answer is for.
    pub updated: String,
    pub checked: String,
    /// The file it was found in.
    pub file: Option<CatalogFile>,
}

/// A file of a mod (its newest version).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogFile {
    /// The mod file (update chain) id.
    pub file_id: String,
    pub file_name: String,
    /// The version's global id and game-scoped id (what nxm links and the files tab name).
    pub version_id: String,
    pub version_gsid: String,
    pub version: String,
    pub category: String,
    pub uploaded: String,
    pub primary: bool,
    pub kind: ModType,
    /// The archive's file name (the version's name on Nexus).
    pub archive: String,
}

/// A mod as the catalog lists it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogMod {
    /// The game-scoped id (the number in the page URL).
    pub id: String,
    /// Nexus's global id.
    pub uid: String,
    pub name: String,
    pub summary: String,
    pub version: Option<String>,
    pub author: String,
    pub category: Option<String>,
    pub thumbnail: Option<String>,
    pub endorsements: u64,
    pub downloads: u64,
    pub created: String,
    pub updated: String,
    pub adult: bool,
    /// None: not looked at (older than Kiln, or no API key).
    pub kind: Option<ModType>,
    pub url: String,
    pub installed: Option<InstalledInfo>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledInfo {
    pub version: String,
    pub version_gsid: String,
    pub file_name: String,
    /// Still in the library (a removed library copy makes it "not installed" again).
    pub in_library: bool,
    pub latest_version: Option<String>,
    pub latest_gsid: Option<String>,
    pub update_available: bool,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Filter {
    /// Kiln mods only.
    #[default]
    Kiln,
    /// Every Enshrouded mod.
    All,
    /// Mods installed from the catalog.
    Installed,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Sort {
    #[default]
    Endorsements,
    Updated,
    New,
    Downloads,
    Name,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Query {
    pub text: String,
    pub filter: Filter,
    pub sort: Sort,
    /// 1-based.
    pub page: u32,
    /// Default 24, at most 100.
    pub page_size: u32,
    pub adult: bool,
    /// Fetch the list again now (Refresh).
    pub refresh: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Page {
    pub mods: Vec<CatalogMod>,
    pub total: usize,
    pub page: u32,
    pub page_size: u32,
    /// Mods that may be Kiln mods and were not looked at yet (ask again to check the next batch).
    pub unchecked: usize,
    /// The Kiln filter needs the API key.
    pub needs_key: bool,
    /// When the mod list was fetched.
    pub fetched: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Image {
    pub url: String,
    pub thumb: Option<String>,
    pub title: Option<String>,
}

/// A mod another mod's file requires (Nexus's file requirements).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Requirement {
    /// Game-scoped id of the required mod.
    pub mod_id: String,
    pub name: String,
    pub file_name: String,
    pub min_version: Option<String>,
    /// Installed from the catalog already.
    pub installed: bool,
    /// It is Kiln itself (the launcher is that).
    pub kiln: bool,
    /// Another game's mod (shown, not offered).
    pub other_game: bool,
    pub url: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Details {
    #[serde(flatten)]
    pub summary: CatalogMod,
    /// The description as plain text (BBCode removed).
    pub description: String,
    /// The page's free-text author credit (the summary's `author` is the uploader).
    pub credit: Option<String>,
    pub tags: Vec<String>,
    pub unique_downloads: u64,
    pub images: Vec<Image>,
    /// Active files, newest version each, primary first.
    pub files: Vec<CatalogFile>,
    /// The file "Add" takes (the Kiln file, else the primary, else the newest main file).
    pub best_file: Option<CatalogFile>,
    pub requirements: Vec<Requirement>,
    pub files_url: String,
    /// Files and requirements need the API key.
    pub needs_key: bool,
}

/// A mod installed from the catalog (`<data>/nexus/installs.json`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallRecord {
    pub mod_id: String,
    pub name: String,
    pub file_id: String,
    pub file_name: String,
    pub version_id: String,
    pub version_gsid: String,
    pub version: String,
    pub refs: Vec<LibraryRef>,
    pub installed: String,
    /// "nxm" (a Mod Manager Download link) or "catalog" (premium, from the launcher).
    pub source: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Installs {
    format: u32,
    mods: Vec<InstallRecord>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Latest {
    version: String,
    version_gsid: String,
    checked: i64,
}

/// An installed catalog mod with its update state.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledMod {
    #[serde(flatten)]
    pub record: InstallRecord,
    pub info: InstalledInfo,
}

pub struct Catalog {
    root: PathBuf,
    client: Client,
    list: Mutex<Option<(Instant, String, Vec<ModSummary>)>>,
    verdicts: Mutex<Option<BTreeMap<String, Verdict>>>,
}

impl std::fmt::Debug for Catalog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Catalog").field("root", &self.root).field("client", &self.client).finish()
    }
}

/// `<data>/nexus`
pub fn root_of(dirs: &DataDirs) -> PathBuf {
    dirs.root.join("nexus")
}

impl Catalog {
    pub fn new(root: PathBuf, client: Client) -> Catalog {
        Catalog { root, client, list: Mutex::new(None), verdicts: Mutex::new(None) }
    }

    pub fn client(&self) -> &Client {
        &self.client
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    // ------------------------------------------------------------ the list

    fn all_mods(&self, adult: bool, refresh: bool) -> Result<(Vec<ModSummary>, String)> {
        {
            let l = self.list.lock().unwrap();
            if let Some((at, when, mods)) = l.as_ref()
                && !refresh
                && at.elapsed() < LIST_TTL
            {
                return Ok((mods.iter().filter(|m| adult || !m.adult).cloned().collect(), when.clone()));
            }
        }
        let mut mods = Vec::new();
        let mut cursor: Option<String> = None;
        for _ in 0..MAX_LIST_PAGES {
            let page = self.client.search(GAME, "endorsements", "include", cursor.as_deref(), 100)?;
            let n = page.mods.len();
            mods.extend(page.mods);
            cursor = page.next_cursor;
            if cursor.is_none() || n == 0 {
                break;
            }
        }
        // a cursor repeats a mod that moved between pages
        let mut seen = std::collections::HashSet::new();
        mods.retain(|m| seen.insert(m.id.clone()));
        let when = crate::util::now_rfc3339();
        *self.list.lock().unwrap() = Some((Instant::now(), when.clone(), mods.clone()));
        Ok((mods.into_iter().filter(|m| adult || !m.adult).collect(), when))
    }

    fn verdict_path(&self) -> PathBuf {
        self.root.join("verdicts.json")
    }

    fn with_verdicts<T>(&self, f: impl FnOnce(&mut BTreeMap<String, Verdict>) -> T) -> T {
        let mut v = self.verdicts.lock().unwrap();
        if v.is_none() {
            #[derive(Deserialize)]
            struct F {
                mods: BTreeMap<String, Verdict>,
            }
            *v = Some(crate::util::read_json::<F>(&self.verdict_path()).ok().flatten().map(|f| f.mods).unwrap_or_default());
        }
        f(v.as_mut().unwrap())
    }

    fn save_verdicts(&self) {
        #[derive(Serialize)]
        struct F<'a> {
            format: u32,
            mods: &'a BTreeMap<String, Verdict>,
        }
        let v = self.verdicts.lock().unwrap();
        if let Some(m) = v.as_ref() {
            let _ = std::fs::create_dir_all(&self.root);
            let _ = crate::util::write_json_atomic(&self.verdict_path(), &F { format: 1, mods: m });
        }
    }

    /// A cached verdict still valid for this `updated_at`.
    pub fn cached_verdict(&self, uid: &str, updated: &str) -> Option<Verdict> {
        self.with_verdicts(|v| v.get(uid).filter(|x| x.updated == updated).cloned())
    }

    fn may_be_kiln(m: &ModSummary) -> bool {
        m.game_scoped_id != KILN_MOD && newer_than(&m.updated_at, KILN_EPOCH)
    }

    /// The active files of a mod with their newest listed version, most recently updated first.
    pub fn files(&self, mod_uid: &str) -> Result<Vec<(ModFile, FileVersion)>> {
        let mut files: Vec<ModFile> = self.client.mod_files(mod_uid)?.into_iter().filter(|f| f.is_active).collect();
        files.sort_by(|a, b| b.last_file_uploaded_at.cmp(&a.last_file_uploaded_at));
        let mut out = Vec::new();
        for f in files.into_iter().take(8) {
            let versions = self.client.file_versions(&f.id)?;
            if let Some(v) = newest(&versions) {
                out.push((f, v));
            }
        }
        Ok(out)
    }

    /// Looks at a mod's files (at most three manifests) and remembers what it is.
    pub fn verify(&self, m: &ModSummary) -> Result<Verdict> {
        let files = self.files(&m.id)?;
        self.verdict_from_files(&m.id, &m.updated_at, &files)
    }

    fn verdict_from_files(&self, uid: &str, updated: &str, files: &[(ModFile, FileVersion)]) -> Result<Verdict> {
        let mut best: Option<(ModType, CatalogFile)> = None;
        for (f, v) in files.iter().take(FILES_CHECKED) {
            let kind = match v.manifest_url.as_deref() {
                Some(u) => match self.client.manifest(u) {
                    Ok(entries) => classify(&entries.iter().map(|e| e.path.as_str()).collect::<Vec<_>>()),
                    Err(_) => continue,
                },
                None => classify(&[v.name.as_str()]),
            };
            let cf = catalog_file(f, v, kind);
            if best.as_ref().is_none_or(|(k, _)| rank(kind) < rank(*k)) {
                best = Some((kind, cf));
            }
            if kind == ModType::Kiln {
                break;
            }
        }
        let verdict = Verdict { kind: best.as_ref().map_or(ModType::Other, |b| b.0), updated: updated.to_string(), checked: crate::util::now_rfc3339(), file: best.map(|b| b.1) };
        self.with_verdicts(|all| all.insert(uid.to_string(), verdict.clone()));
        self.save_verdicts();
        Ok(verdict)
    }

    /// The catalog page for a query. With the Kiln filter, up to [`CHECK_BATCH`] mods that may be Kiln mods are looked
    /// at per call; `unchecked` says how many still wait.
    pub fn search(&self, dirs: &DataDirs, q: &Query) -> Result<Page> {
        let (mods, fetched) = self.all_mods(q.adult, q.refresh)?;
        let text = q.text.trim().to_lowercase();
        let words: Vec<&str> = text.split_whitespace().collect();
        let matches = |m: &ModSummary| {
            words.is_empty() || {
                let hay = format!("{} {} {} {}", m.name, m.summary.as_deref().unwrap_or(""), m.author.as_ref().and_then(|a| a.name.as_deref()).unwrap_or(""),
                    m.category.as_ref().and_then(|c| c.name.as_deref()).unwrap_or("")).to_lowercase();
                words.iter().all(|w| hay.contains(w))
            }
        };
        let mut list: Vec<&ModSummary> = mods.iter().filter(|m| matches(m)).collect();
        sort(&mut list, q.sort);
        let installed = self.installed(dirs).unwrap_or_default();
        let inst_of = |gsid: &str| installed.iter().find(|i| i.record.mod_id == gsid).map(|i| i.info.clone());
        let mut needs_key = false;
        let mut unchecked = 0;
        let keep: Vec<&ModSummary> = match q.filter {
            Filter::All => list,
            Filter::Installed => list.into_iter().filter(|m| inst_of(&m.game_scoped_id).is_some()).collect(),
            Filter::Kiln => {
                if !self.client.has_key() {
                    needs_key = true;
                    vec![]
                } else {
                    let mut budget = CHECK_BATCH;
                    let mut out = Vec::new();
                    for m in list.into_iter().filter(|m| Self::may_be_kiln(m)) {
                        let v = match self.cached_verdict(&m.id, &m.updated_at) {
                            Some(v) => Some(v),
                            None if budget > 0 => {
                                budget -= 1;
                                match self.verify(m) {
                                    Ok(v) => Some(v),
                                    Err(e) if matches!(e.code, ErrorCode::AuthFailed | ErrorCode::Busy) => return Err(e),
                                    Err(_) => None,
                                }
                            }
                            None => {
                                unchecked += 1;
                                None
                            }
                        };
                        if v.is_some_and(|v| v.kind == ModType::Kiln) {
                            out.push(m);
                        }
                    }
                    out
                }
            }
        };
        let page_size = if q.page_size == 0 { 24 } else { q.page_size.min(100) };
        let page = q.page.max(1);
        let total = keep.len();
        let mods = keep.into_iter().skip(((page - 1) * page_size) as usize).take(page_size as usize)
            .map(|m| {
                let kind = self.cached_verdict(&m.id, &m.updated_at).map(|v| v.kind);
                to_catalog_mod(m, kind, inst_of(&m.game_scoped_id))
            })
            .collect();
        Ok(Page { mods, total, page, page_size, unchecked, needs_key, fetched })
    }

    // ------------------------------------------------------------ details

    /// One mod's page, files and requirements (files and requirements only with the key).
    pub fn details(&self, dirs: &DataDirs, gsid: &str) -> Result<Details> {
        let d: ModDetails = self.client.mod_details(GAME, gsid)?;
        let installed = self.installed(dirs).unwrap_or_default();
        let inst = installed.iter().find(|i| i.record.mod_id == d.game_scoped_id).map(|i| i.info.clone());
        let summary_from_list = self.list.lock().unwrap().as_ref().and_then(|(_, _, l)| l.iter().find(|m| m.game_scoped_id == d.game_scoped_id).cloned());
        let updated = summary_from_list.as_ref().map(|s| s.updated_at.clone()).unwrap_or_default();
        let mut files = Vec::new();
        let mut kind = summary_from_list.as_ref().and_then(|s| self.cached_verdict(&s.id, &s.updated_at)).map(|v| v.kind);
        let mut best = None;
        let mut requirements = Vec::new();
        let needs_key = !self.client.has_key();
        if !needs_key {
            let fv = self.files(&d.id)?;
            let primary = d.primary_file_version.as_ref().map(|p| p.id.clone());
            let v = self.verdict_from_files(&d.id, if updated.is_empty() { "?" } else { &updated }, &fv)?;
            kind = Some(v.kind);
            for (f, ver) in &fv {
                let mut cf = catalog_file(f, ver, if v.file.as_ref().is_some_and(|x| x.file_id == f.id) { v.kind } else { ModType::Other });
                cf.primary = cf.primary || primary.as_deref() == Some(ver.id.as_str());
                files.push(cf);
            }
            files.sort_by(|a, b| b.primary.cmp(&a.primary).then(rank_category(&a.category).cmp(&rank_category(&b.category))).then(b.uploaded.cmp(&a.uploaded)));
            best = v.file.clone().filter(|f| f.kind != ModType::Other)
                .or_else(|| files.iter().find(|f| f.primary).cloned())
                .or_else(|| files.iter().find(|f| f.category == "main").cloned())
                .or_else(|| files.first().cloned());
            if let Some(b) = &best {
                requirements = self.requirements(&b.version_id, &installed);
            }
        }
        let summary = CatalogMod {
            id: d.game_scoped_id.clone(),
            uid: d.id.clone(),
            name: d.name.clone(),
            summary: summary_from_list.as_ref().and_then(|s| s.summary.clone()).unwrap_or_default(),
            version: d.version.clone(),
            author: d.uploader.as_ref().and_then(|u| u.name.clone()).or_else(|| d.author.clone()).unwrap_or_default(),
            category: d.category.as_ref().and_then(|c| c.name.clone()),
            thumbnail: d.primary_image.as_ref().and_then(|i| i.thumbnail_url.clone()),
            endorsements: d.stats.endorsements,
            downloads: d.stats.downloads,
            created: summary_from_list.as_ref().map(|s| s.created_at.clone()).unwrap_or_default(),
            updated,
            adult: d.adult_content,
            kind,
            url: mod_url(&d.game_scoped_id),
            installed: inst,
        };
        let mut images: Vec<Image> = d.gallery_images.iter().map(|i| Image { url: i.url.clone(), thumb: i.large_thumbnail_url.clone().or_else(|| i.thumbnail_url.clone()), title: i.title.clone().filter(|t| !t.is_empty()) }).collect();
        if images.is_empty() && let Some(p) = &d.primary_image {
            images.push(Image { url: p.url.clone(), thumb: p.large_thumbnail_url.clone(), title: None });
        }
        Ok(Details {
            description: bbcode_to_text(d.description.as_deref().unwrap_or("")),
            credit: d.author.clone().filter(|a| !a.is_empty()),
            tags: d.tags.iter().filter_map(|t| t.name.clone()).collect(),
            unique_downloads: d.stats.unique_downloads,
            images,
            files_url: files_url(&d.game_scoped_id, best.as_ref().map(|b| b.version_gsid.as_str())),
            files,
            best_file: best,
            requirements,
            needs_key,
            summary,
        })
    }

    /// Nexus's file requirements of one version (first alternative of each), Kiln and other games marked.
    pub fn requirements(&self, version_id: &str, installed: &[InstalledMod]) -> Vec<Requirement> {
        let Ok(defs) = self.client.dependencies(version_id) else { return vec![] };
        let mut out: Vec<Requirement> = Vec::new();
        for d in defs {
            let Some(r) = d.ranges.first() else { continue };
            let m = &r.target_mod_file.r#mod;
            let other_game = m.game.as_ref().is_some_and(|g| !g.domain_name.is_empty() && !g.domain_name.eq_ignore_ascii_case(GAME));
            if out.iter().any(|x| x.mod_id == m.game_scoped_id) {
                continue;
            }
            out.push(Requirement {
                mod_id: m.game_scoped_id.clone(),
                name: m.name.clone(),
                file_name: r.target_mod_file.name.clone(),
                min_version: r.min_version.as_ref().map(|v| v.version.clone()).filter(|v| !v.is_empty()),
                installed: installed.iter().any(|i| i.record.mod_id == m.game_scoped_id && i.info.in_library),
                kiln: !other_game && m.game_scoped_id == KILN_MOD,
                other_game,
                url: if other_game { m.game.as_ref().map(|g| format!("https://www.nexusmods.com/{}/mods/{}", g.domain_name, m.game_scoped_id)).unwrap_or_default() } else { mod_url(&m.game_scoped_id) },
            });
        }
        out
    }

    // ------------------------------------------------------------ installs and updates

    fn installs_path(&self) -> PathBuf {
        self.root.join("installs.json")
    }

    fn latest_path(&self) -> PathBuf {
        self.root.join("latest.json")
    }

    fn read_installs(&self) -> Installs {
        crate::util::read_json::<Installs>(&self.installs_path()).ok().flatten().unwrap_or_default()
    }

    fn read_latest(&self) -> BTreeMap<String, Latest> {
        crate::util::read_json::<BTreeMap<String, Latest>>(&self.latest_path()).ok().flatten().unwrap_or_default()
    }

    /// Remembers an install (one record per mod: a newer one replaces it).
    pub fn record_install(&self, rec: InstallRecord) -> Result<()> {
        let mut i = self.read_installs();
        i.format = 1;
        i.mods.retain(|m| m.mod_id != rec.mod_id);
        i.mods.push(rec);
        i.mods.sort_by_key(|m| m.name.to_lowercase());
        std::fs::create_dir_all(&self.root).map_err(crate::util::io_at(&self.root, "creating"))?;
        crate::util::write_json_atomic(&self.installs_path(), &i)
    }

    /// The mods installed from the catalog, with the newest versions last seen (no network).
    pub fn installed(&self, dirs: &DataDirs) -> Result<Vec<InstalledMod>> {
        let recs = self.read_installs().mods;
        if recs.is_empty() {
            return Ok(vec![]);
        }
        let library = crate::library::list(dirs, None).unwrap_or_default();
        let has = |r: &LibraryRef| library.iter().any(|e| e.kind == r.kind && e.id == r.id && e.versions.iter().any(|v| v.version == r.version && v.hash == r.hash));
        let latest = self.read_latest();
        Ok(recs.into_iter().map(|record| {
            let l = latest.get(&record.file_id);
            // the file's newest version is another upload, and not an older version string (a re-upload of the same
            // version counts: its position in the file is newer)
            let update = l.is_some_and(|l| l.version_gsid != record.version_gsid && !version::is_newer(&record.version, &l.version));
            let info = InstalledInfo {
                version: record.version.clone(),
                version_gsid: record.version_gsid.clone(),
                file_name: record.file_name.clone(),
                in_library: !record.refs.is_empty() && record.refs.iter().any(has),
                latest_version: l.map(|l| l.version.clone()),
                latest_gsid: l.map(|l| l.version_gsid.clone()),
                update_available: update,
            };
            InstalledMod { record, info }
        }).collect())
    }

    /// Looks up the newest version of each installed mod's file (at most every 30 minutes per file, unless `force`).
    pub fn check_updates(&self, dirs: &DataDirs, force: bool) -> Result<Vec<InstalledMod>> {
        let recs = self.read_installs().mods;
        let mut latest = self.read_latest();
        let now = crate::util::now_unix();
        let mut first_err = None;
        for r in &recs {
            if !force && latest.get(&r.file_id).is_some_and(|l| now - l.checked < LATEST_TTL_SECS) {
                continue;
            }
            match self.client.file_versions(&r.file_id) {
                Ok(vs) => {
                    if let Some(v) = newest(&vs) {
                        latest.insert(r.file_id.clone(), Latest { version: v.version, version_gsid: v.game_scoped_id, checked: now });
                    }
                }
                Err(e) if matches!(e.code, ErrorCode::AuthFailed | ErrorCode::Busy) => {
                    first_err = Some(e);
                    break;
                }
                Err(_) => {}
            }
        }
        if !latest.is_empty() {
            std::fs::create_dir_all(&self.root).map_err(crate::util::io_at(&self.root, "creating"))?;
            crate::util::write_json_atomic(&self.latest_path(), &latest)?;
        }
        if let Some(e) = first_err {
            return Err(e);
        }
        self.installed(dirs)
    }
}

/// The newest listed version of a file (highest position; archived and removed versions left out).
pub fn newest(versions: &[FileVersion]) -> Option<FileVersion> {
    versions.iter().filter(|v| v.is_listed()).max_by(|a, b| a.position_value().partial_cmp(&b.position_value()).unwrap_or(std::cmp::Ordering::Equal)
        .then_with(|| a.uploaded_at.cmp(&b.uploaded_at))).cloned()
}

fn catalog_file(f: &ModFile, v: &FileVersion, kind: ModType) -> CatalogFile {
    CatalogFile {
        file_id: f.id.clone(),
        file_name: if f.name.is_empty() { v.file.name.clone() } else { f.name.clone() },
        version_id: v.id.clone(),
        version_gsid: v.game_scoped_id.clone(),
        version: v.version.clone(),
        category: v.category.clone(),
        uploaded: v.uploaded_at.clone(),
        primary: v.is_primary,
        kind,
        archive: v.name.clone(),
    }
}

fn rank(k: ModType) -> u8 {
    match k {
        ModType::Kiln => 0,
        ModType::Native => 1,
        ModType::Emf => 2,
        ModType::Modpack => 3,
        ModType::Dll => 4,
        ModType::Other => 5,
    }
}

fn rank_category(c: &str) -> u8 {
    match c {
        "main" => 0,
        "update" => 1,
        "optional" => 2,
        _ => 3,
    }
}

/// What an archive holds, from its file list.
pub fn classify(paths: &[&str]) -> ModType {
    let names: Vec<String> = paths.iter().map(|p| p.replace('\\', "/").rsplit('/').next().unwrap_or("").to_ascii_lowercase()).collect();
    let has = |n: &str| names.iter().any(|x| x == n);
    if has("kiln.mod.json") {
        ModType::Kiln
    } else if has("kiln-modpack.json") {
        ModType::Modpack
    } else if has("native.json") {
        ModType::Native
    } else if has("mod.json") && names.iter().any(|x| x.ends_with(".dll")) {
        ModType::Emf
    } else if names.iter().any(|x| x.ends_with(".dll")) {
        ModType::Dll
    } else {
        ModType::Other
    }
}

fn newer_than(a: &str, b: &str) -> bool {
    match (crate::util::parse_rfc3339(a), crate::util::parse_rfc3339(b)) {
        (Some(x), Some(y)) => x >= y,
        _ => true,
    }
}

fn sort(list: &mut [&ModSummary], s: Sort) {
    match s {
        Sort::Endorsements => list.sort_by(|a, b| b.endorsements.cmp(&a.endorsements).then(b.downloads.cmp(&a.downloads))),
        Sort::Downloads => list.sort_by_key(|m| std::cmp::Reverse(m.downloads)),
        Sort::Updated => list.sort_by(|a, b| b.updated_at.cmp(&a.updated_at)),
        Sort::New => list.sort_by(|a, b| b.created_at.cmp(&a.created_at)),
        Sort::Name => list.sort_by_key(|m| m.name.to_lowercase()),
    }
}

fn to_catalog_mod(m: &ModSummary, kind: Option<ModType>, installed: Option<InstalledInfo>) -> CatalogMod {
    CatalogMod {
        id: m.game_scoped_id.clone(),
        uid: m.id.clone(),
        name: m.name.clone(),
        summary: m.summary.clone().unwrap_or_default(),
        version: m.version.clone(),
        author: m.author.as_ref().and_then(|a| a.name.clone()).unwrap_or_default(),
        category: m.category.as_ref().and_then(|c| c.name.clone()),
        thumbnail: m.thumbnail_url.clone(),
        endorsements: m.endorsements,
        downloads: m.downloads,
        created: m.created_at.clone(),
        updated: m.updated_at.clone(),
        adult: m.adult,
        kind,
        url: mod_url(&m.game_scoped_id),
        installed,
    }
}

/// BBCode (and the `<br />` the site stores) to plain text: tags removed, list items as "• ", links as their text,
/// HTML entities decoded, at most one empty line in a row.
pub fn bbcode_to_text(s: &str) -> String {
    let s = s.replace("<br />", "\n").replace("<br/>", "\n").replace("<br>", "\n").replace("\r\n", "\n");
    let mut out = String::new();
    let mut rest = s.as_str();
    while let Some(i) = rest.find('[') {
        out.push_str(&rest[..i]);
        let tail = &rest[i..];
        match tail.find(']') {
            Some(j) if j < 120 && !tail[1..j].contains('[') => {
                let tag = tail[1..j].trim().to_ascii_lowercase();
                if tag == "*" {
                    out.push_str("\n• ");
                } else if tag == "/list" || tag == "/quote" || tag == "/center" || tag == "line" || tag == "hr" {
                    out.push('\n');
                } else if tag.starts_with("img") {
                    // the image URL is not text: skip to [/img]
                    let after = &tail[j + 1..];
                    rest = after.find("[/img]").map_or(after, |k| &after[k + 6..]);
                    continue;
                }
                rest = &tail[j + 1..];
            }
            _ => {
                out.push('[');
                rest = &tail[1..];
            }
        }
    }
    out.push_str(rest);
    let out = out.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&#39;", "'").replace("&nbsp;", " ");
    let mut clean = String::new();
    let mut blank = 0;
    for line in out.lines() {
        let l = line.trim_end();
        if l.trim().is_empty() {
            blank += 1;
            if blank > 1 || clean.is_empty() {
                continue;
            }
        } else {
            blank = 0;
        }
        clean.push_str(l.trim_start_matches(' '));
        clean.push('\n');
    }
    clean.trim().to_string()
}

/// Kiln's own mod is never an install target from the catalog (the launcher is Kiln).
pub fn refuse_kiln(gsid: &str) -> Result<()> {
    if gsid == KILN_MOD {
        return Err(Error::new(ErrorCode::Refused, "That is Kiln itself: update the launcher from its page instead."));
    }
    Ok(())
}
