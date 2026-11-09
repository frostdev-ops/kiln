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
//! File metadata needs account authorization (anonymous calls get search and mod pages only): without one the
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
    /// None: not looked at (older than Kiln, or signed out).
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
    /// The Kiln filter needs sign-in.
    pub needs_sign_in: bool,
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
    /// Files and requirements need sign-in.
    pub needs_sign_in: bool,
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
        // Hold the list lock while fetching as well: two UI searches must share one paged request, rather than
        // independently spending the same API budget and racing to replace the cache.
        let mut l = self.list.lock().unwrap();
        if let Some((at, when, mods)) = l.as_ref()
                && !refresh
                && at.elapsed() < LIST_TTL
        {
            return Ok((mods.iter().filter(|m| adult || !m.adult).cloned().collect(), when.clone()));
        }
        let mut mods = Vec::new();
        let mut cursor: Option<String> = None;
        let mut cursors = std::collections::HashSet::new();
        for _ in 0..MAX_LIST_PAGES {
            let page = self.client.search(GAME, "endorsements", "include", cursor.as_deref(), 100)?;
            let n = page.mods.len();
            mods.extend(page.mods);
            cursor = page.next_cursor;
            if cursor.is_none() || n == 0 {
                break;
            }
            if !cursors.insert(cursor.clone()) {
                return Err(Error::new(ErrorCode::Network, "Nexus Mods repeated a catalog cursor. Try Refresh again."));
            }
        }
        if cursor.is_some() {
            return Err(Error::new(ErrorCode::Refused, "Nexus Mods' catalog exceeded the page limit. The incomplete list was not cached."));
        }
        // a cursor repeats a mod that moved between pages
        let mut seen = std::collections::HashSet::new();
        mods.retain(|m| seen.insert(m.id.clone()));
        let when = crate::util::now_rfc3339();
        *l = Some((Instant::now(), when.clone(), mods.clone()));
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
        let Ok(_lock) = super::handler::lock_state(&self.root, "catalog.lock") else { return };
        let v = self.verdicts.lock().unwrap();
        if let Some(m) = v.as_ref() {
            #[derive(Deserialize)]
            struct Stored {
                mods: BTreeMap<String, Verdict>,
            }
            let mut merged = crate::util::read_json::<Stored>(&self.verdict_path()).ok().flatten().map(|f| f.mods).unwrap_or_default();
            for (uid, verdict) in m {
                if merged.get(uid).is_none_or(|old| old.checked <= verdict.checked) {
                    merged.insert(uid.clone(), verdict.clone());
                }
            }
            let _ = crate::util::write_json_atomic(&self.verdict_path(), &F { format: 1, mods: &merged });
        }
    }

    /// A cached verdict still valid for this `updated_at`.
    pub fn cached_verdict(&self, uid: &str, updated: &str) -> Option<Verdict> {
        if !super::enabled() { return None }
        self.with_verdicts(|v| v.get(uid).filter(|x| x.updated == updated).cloned())
    }

    fn may_be_kiln(m: &ModSummary) -> bool {
        m.game_scoped_id != KILN_MOD && newer_than(&m.updated_at, KILN_EPOCH)
    }

    /// The active files of a mod with their newest listed version, most recently updated first.
    pub fn files(&self, mod_uid: &str) -> Result<Vec<(ModFile, FileVersion)>> {
        super::require_enabled()?;
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
        super::require_enabled()?;
        let files = self.files(&m.id)?;
        self.verdict_from_files(&m.id, &m.updated_at, &files)
    }

    fn verdict_from_files(&self, uid: &str, updated: &str, files: &[(ModFile, FileVersion)]) -> Result<Verdict> {
        let mut best: Option<(ModType, CatalogFile)> = None;
        for (f, v) in files.iter().take(FILES_CHECKED) {
            let kind = match v.manifest_url.as_deref() {
                // An unavailable manifest does not prove that a mod is not a Kiln mod. Propagate the error so a
                // later request can retry instead of persisting an "Other" verdict until the author updates it.
                Some(u) => classify(&self.client.manifest(u)?.iter().map(|e| e.path.as_str()).collect::<Vec<_>>()),
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
        super::require_enabled()?;
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
        let mut needs_sign_in = false;
        let mut unchecked = 0;
        let keep: Vec<&ModSummary> = match q.filter {
            Filter::All => list,
            Filter::Installed => list.into_iter().filter(|m| inst_of(&m.game_scoped_id).is_some_and(|i| i.in_library)).collect(),
            Filter::Kiln => {
                if !self.client.has_credentials() {
                    needs_sign_in = true;
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
                                    Err(e) => return Err(e),
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
        let offset = u64::from(page - 1) * u64::from(page_size);
        let mods = keep.into_iter().skip(usize::try_from(offset).unwrap_or(usize::MAX)).take(page_size as usize)
            .map(|m| {
                let kind = self.cached_verdict(&m.id, &m.updated_at).map(|v| v.kind);
                to_catalog_mod(m, kind, inst_of(&m.game_scoped_id))
            })
            .collect();
        Ok(Page { mods, total, page, page_size, unchecked, needs_sign_in, fetched })
    }

    // ------------------------------------------------------------ details

    /// One mod's page, files and requirements (files and requirements only with the key).
    pub fn details(&self, dirs: &DataDirs, gsid: &str) -> Result<Details> {
        super::require_enabled()?;
        let d: ModDetails = self.client.mod_details(GAME, gsid)?;
        let installed = self.installed(dirs).unwrap_or_default();
        let inst = installed.iter().find(|i| i.record.mod_id == d.game_scoped_id).map(|i| i.info.clone());
        let summary_from_list = self.list.lock().unwrap().as_ref().and_then(|(_, _, l)| l.iter().find(|m| m.game_scoped_id == d.game_scoped_id).cloned());
        let updated = summary_from_list.as_ref().map(|s| s.updated_at.clone()).unwrap_or_default();
        let mut files = Vec::new();
        let mut kind = summary_from_list.as_ref().and_then(|s| self.cached_verdict(&s.id, &s.updated_at)).map(|v| v.kind);
        let mut best = None;
        let mut requirements = Vec::new();
        let needs_sign_in = !self.client.has_credentials();
        if !needs_sign_in {
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
            needs_sign_in,
            summary,
        })
    }

    /// Nexus's file requirements of one version (first alternative of each), Kiln and other games marked.
    pub fn requirements(&self, version_id: &str, installed: &[InstalledMod]) -> Vec<Requirement> {
        if !super::enabled() { return Vec::new() }
        let Ok(defs) = self.client.dependencies(version_id) else { return vec![] };
        let mut out: Vec<Requirement> = Vec::new();
        for d in defs {
            let Some(r) = d.ranges.first() else { continue };
            let m = &r.target_mod_file.r#mod;
            let other_game = m.game.as_ref().is_some_and(|g| !g.domain_name.is_empty() && !g.domain_name.eq_ignore_ascii_case(GAME));
            let url = if other_game { m.game.as_ref().map(|g| format!("https://www.nexusmods.com/{}/mods/{}", g.domain_name, m.game_scoped_id)).unwrap_or_default() } else { mod_url(&m.game_scoped_id) };
            if out.iter().any(|x| x.url == url) {
                continue;
            }
            out.push(Requirement {
                mod_id: m.game_scoped_id.clone(),
                name: m.name.clone(),
                file_name: r.target_mod_file.name.clone(),
                min_version: r.min_version.as_ref().map(|v| v.version.clone()).filter(|v| !v.is_empty()),
                installed: !other_game && installed.iter().any(|i| i.record.mod_id == m.game_scoped_id && i.info.in_library),
                kiln: !other_game && m.game_scoped_id == KILN_MOD,
                other_game,
                url,
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

    fn read_installs(&self) -> Result<Installs> {
        Ok(crate::util::read_json::<Installs>(&self.installs_path())?.unwrap_or_default())
    }

    fn read_latest(&self) -> BTreeMap<String, Latest> {
        crate::util::read_json::<BTreeMap<String, Latest>>(&self.latest_path()).ok().flatten().unwrap_or_default()
    }

    /// Remembers an install (one record per mod: a newer one replaces it).
    pub fn record_install(&self, rec: InstallRecord) -> Result<()> {
        super::require_enabled()?;
        let _lock = super::handler::lock_state(&self.root, "catalog.lock")?;
        let mut i = self.read_installs()?;
        // A previous update check may predate this install (including a re-upload of the same version). Never
        // advertise that stale upload as an update over the file the user just installed.
        let mut latest = self.read_latest();
        latest.remove(&rec.file_id);
        crate::util::write_json_atomic(&self.latest_path(), &latest)?;
        i.format = 1;
        i.mods.retain(|m| m.mod_id != rec.mod_id);
        i.mods.push(rec);
        i.mods.sort_by_key(|m| m.name.to_lowercase());
        crate::util::write_json_atomic(&self.installs_path(), &i)
    }

    /// The mods installed from the catalog, with the newest versions last seen (no network).
    pub fn installed(&self, dirs: &DataDirs) -> Result<Vec<InstalledMod>> {
        super::require_enabled()?;
        let recs = self.read_installs()?.mods;
        if recs.is_empty() {
            return Ok(vec![]);
        }
        let library = crate::library::list(dirs, None).unwrap_or_default();
        let has = |r: &LibraryRef| library.iter().any(|e| e.kind == r.kind && e.id == r.id && e.versions.iter().any(|v| v.version == r.version && v.hash == r.hash));
        let latest = self.read_latest();
        Ok(recs.into_iter().map(|record| {
            let l = latest.get(&record.file_id);
            let in_library = !record.refs.is_empty() && record.refs.iter().any(has);
            // the file's newest version is another upload, and not an older version string (a re-upload of the same
            // version counts: its position in the file is newer)
            let update = in_library && l.is_some_and(|l| l.version_gsid != record.version_gsid && !version::is_newer(&record.version, &l.version));
            let info = InstalledInfo {
                version: record.version.clone(),
                version_gsid: record.version_gsid.clone(),
                file_name: record.file_name.clone(),
                in_library,
                latest_version: l.map(|l| l.version.clone()),
                latest_gsid: l.map(|l| l.version_gsid.clone()),
                update_available: update,
            };
            InstalledMod { record, info }
        }).collect())
    }

    /// Looks up the newest version of each installed mod's file (at most every 30 minutes per file, unless `force`).
    pub fn check_updates(&self, dirs: &DataDirs, force: bool) -> Result<Vec<InstalledMod>> {
        super::require_enabled()?;
        let _lock = super::handler::lock_state(&self.root, "catalog.lock")?;
        let recs = self.read_installs()?.mods;
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
                Err(e) => {
                    first_err = Some(e);
                    break;
                }
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
    versions.iter().filter(|v| v.is_listed() && position(&v.position).is_some()).max_by(|a, b| compare_positions(&a.position, &b.position)
        .then_with(|| a.uploaded_at.cmp(&b.uploaded_at))).cloned()
}

fn position(s: &str) -> Option<(&str, &str)> {
    let (whole, fraction) = s.split_once('.').unwrap_or((s, ""));
    if whole.is_empty() || !whole.bytes().all(|b| b.is_ascii_digit()) || !fraction.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    Some((whole.trim_start_matches('0'), fraction.trim_end_matches('0')))
}

fn compare_positions(a: &str, b: &str) -> std::cmp::Ordering {
    let ((aw, af), (bw, bf)) = (position(a).unwrap(), position(b).unwrap());
    aw.len().cmp(&bw.len()).then_with(|| aw.cmp(bw)).then_with(|| {
        af.bytes().chain(std::iter::repeat(b'0')).zip(bf.bytes().chain(std::iter::repeat(b'0')))
            .take(af.len().max(bf.len())).find_map(|(a, b)| (a != b).then(|| a.cmp(&b))).unwrap_or(std::cmp::Ordering::Equal)
    })
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
    if gsid.trim_start_matches('0') == KILN_MOD {
        return Err(Error::new(ErrorCode::Refused, "That is Kiln itself: update the launcher from its page instead."));
    }
    Ok(())
}

#[cfg(test)]
mod regression_tests {
    use super::*;
    use crate::nexus::api::{AppId, HttpRequest, HttpResponse, HttpStream, Transport};
    use serde_json::json;
    use std::sync::{Arc, Barrier};
    use std::sync::atomic::{AtomicUsize, Ordering};

    struct Mock(Box<dyn Fn(&HttpRequest) -> HttpResponse + Send + Sync>);
    impl Transport for Mock {
        fn send(&self, req: &HttpRequest) -> Result<HttpResponse> { Ok((self.0)(req)) }
        fn open(&self, _: &str, _: &[(String, String)]) -> Result<HttpStream> { unreachable!() }
    }

    fn response(v: serde_json::Value) -> HttpResponse {
        HttpResponse { status: 200, body: serde_json::to_vec(&v).unwrap(), ..Default::default() }
    }

    struct Tmp(PathBuf);
    impl Drop for Tmp {
        fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.0); }
    }
    fn tmp() -> Tmp {
        let p = std::env::temp_dir().join(format!("kiln-catalog-review-{}", crate::hash::hex(&crate::util::random_bytes::<16>().unwrap())));
        Tmp(p)
    }
    fn catalog(root: &Path, handler: impl Fn(&HttpRequest) -> HttpResponse + Send + Sync + 'static) -> Catalog {
        Catalog::new(root.to_path_buf(), Client::with_origin("https://api.test", Arc::new(Mock(Box::new(handler))), Some("test-key".into()), AppId::kiln_launcher("test")).without_gap())
    }

    #[test]
    fn manifest_failure_is_retryable_and_not_cached_as_other() {
        let t = tmp();
        let attempts = Arc::new(AtomicUsize::new(0));
        let seen = attempts.clone();
        let cat = catalog(&t.0, move |req| {
            if req.url.ends_with("/mods/4/files") {
                response(json!({"data": {"mod_files": [{"id": "3", "is_active": true}]}}))
            } else if req.url.ends_with("/mod-files/3/versions") {
                response(json!({"data": {"versions": [{"id": "2", "position": "1", "game_scoped_id": "1", "category": "main", "manifest_url": "https://files.test/manifest"}]}}))
            } else if seen.fetch_add(1, Ordering::SeqCst) == 0 {
                HttpResponse { status: 503, ..Default::default() }
            } else {
                response(json!({"files": [{"path": "kiln.mod.json", "size": 10}]}))
            }
        });
        let m = ModSummary { id: "4".into(), game_scoped_id: "5".into(), name: "Test".into(), updated_at: "2026-11-07T00:00:00Z".into(), ..Default::default() };
        assert!(cat.verify(&m).is_err());
        assert!(cat.cached_verdict(&m.id, &m.updated_at).is_none());
        assert_eq!(cat.verify(&m).unwrap().kind, ModType::Kiln);
        assert_eq!(attempts.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn maximum_page_number_is_an_empty_page_not_an_overflow() {
        let t = tmp();
        let cat = catalog(&t.0, |_| response(json!({"data": {"mods": [{"id": "4", "game_scoped_id": "5", "name": "Test"}]}})));
        let page = cat.search(&DataDirs::at(t.0.join("data")), &Query { filter: Filter::All, page: u32::MAX, page_size: 100, ..Default::default() }).unwrap();
        assert_eq!(page.total, 1);
        assert!(page.mods.is_empty());
    }

    #[test]
    fn repeated_cursors_fail_without_caching_an_incomplete_catalog() {
        let t = tmp();
        let calls = Arc::new(AtomicUsize::new(0));
        let seen = calls.clone();
        let cat = catalog(&t.0, move |_| {
            seen.fetch_add(1, Ordering::SeqCst);
            response(json!({"data": {"mods": [{"id": "4", "game_scoped_id": "5", "name": "Test"}]}, "meta": {"next_cursor": "same"}}))
        });
        assert!(cat.all_mods(false, false).is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 2);
        assert!(cat.list.lock().unwrap().is_none());
    }

    #[test]
    fn simultaneous_searches_share_the_list_request() {
        let t = tmp();
        let calls = Arc::new(AtomicUsize::new(0));
        let seen = calls.clone();
        let cat = Arc::new(catalog(&t.0, move |_| {
            seen.fetch_add(1, Ordering::SeqCst);
            std::thread::sleep(Duration::from_millis(20));
            response(json!({"data": {"mods": []}}))
        }));
        let start = Arc::new(Barrier::new(2));
        let workers: Vec<_> = (0..2).map(|_| {
            let cat = cat.clone();
            let start = start.clone();
            std::thread::spawn(move || { start.wait(); cat.all_mods(false, false).unwrap(); })
        }).collect();
        for worker in workers { worker.join().unwrap(); }
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }

    fn record(id: usize) -> InstallRecord {
        InstallRecord { mod_id: id.to_string(), name: format!("mod{id}"), file_id: format!("{id}01"), file_name: "main".into(), version_id: "2".into(), version_gsid: "2".into(), version: "1.0".into(), refs: vec![], installed: "now".into(), source: "catalog".into() }
    }

    #[test]
    fn independent_catalogs_preserve_concurrent_install_records() {
        let t = tmp();
        let start = Arc::new(Barrier::new(20));
        let workers: Vec<_> = (1..=20).map(|id| {
            let root = t.0.clone();
            let start = start.clone();
            std::thread::spawn(move || {
                let cat = catalog(&root, |_| unreachable!());
                start.wait();
                cat.record_install(record(id)).unwrap();
            })
        }).collect();
        for worker in workers { worker.join().unwrap(); }
        let cat = catalog(&t.0, |_| unreachable!());
        assert_eq!(cat.read_installs().unwrap().mods.len(), 20);
    }

    #[test]
    fn installing_invalidates_a_stale_update_check() {
        let t = tmp();
        std::fs::create_dir_all(&t.0).unwrap();
        let cat = catalog(&t.0, |_| unreachable!());
        let rec = record(1);
        crate::util::write_json_atomic(&cat.latest_path(), &BTreeMap::from([(rec.file_id.clone(), Latest { version: rec.version.clone(), version_gsid: "1".into(), checked: crate::util::now_unix() })])).unwrap();
        cat.record_install(rec).unwrap();
        assert!(cat.read_latest().is_empty());
    }

    #[test]
    fn an_update_network_failure_is_visible_to_the_caller() {
        let t = tmp();
        let cat = catalog(&t.0, |_| HttpResponse { status: 503, ..Default::default() });
        cat.record_install(record(1)).unwrap();
        assert!(cat.check_updates(&DataDirs::at(t.0.join("data")), true).is_err());
    }

    #[test]
    fn removed_library_copies_leave_the_installed_filter() {
        let t = tmp();
        let cat = catalog(&t.0, |_| response(json!({"data": {"mods": [{"id": "4", "game_scoped_id": "5", "name": "Test"}]}})));
        cat.record_install(record(5)).unwrap();
        let page = cat.search(&DataDirs::at(t.0.join("data")), &Query { filter: Filter::Installed, ..Default::default() }).unwrap();
        assert_eq!(page.total, 0);
    }

    #[test]
    fn upload_positions_are_exact_decimals_and_invalid_values_are_not_latest() {
        let v = |id: &str, p: &str, date: &str| FileVersion { id: id.into(), position: p.into(), category: "main".into(), uploaded_at: date.into(), ..Default::default() };
        let vs = [v("new", "9007199254740993.000000001", "old"), v("old", "9007199254740993", "new"), v("invalid", "NaN", "newest")];
        assert_eq!(newest(&vs).unwrap().id, "new");
        assert_eq!(compare_positions("0001.0100", "1.01"), std::cmp::Ordering::Equal);
        assert!(position("1.0.0").is_none());
        assert!(position("inf").is_none());
    }

    #[test]
    fn kiln_self_install_guard_handles_zero_padded_ids() {
        assert_eq!(refuse_kiln("000301").unwrap_err().code, ErrorCode::Refused);
        assert!(refuse_kiln("310").is_ok());
    }

    #[test]
    fn malformed_install_records_are_preserved_for_recovery() {
        let t = tmp();
        std::fs::create_dir_all(&t.0).unwrap();
        let cat = catalog(&t.0, |_| unreachable!());
        std::fs::write(cat.installs_path(), b"damaged records").unwrap();
        assert!(cat.record_install(record(1)).is_err());
        assert_eq!(std::fs::read(cat.installs_path()).unwrap(), b"damaged records");
    }

    #[test]
    fn requirement_identity_includes_its_game() {
        let t = tmp();
        let cat = catalog(&t.0, |_| response(json!({"dependency_definitions": [
            {"id": "1", "ranges": [{"target_mod_file": {"id": "10", "mod": {"id": "20", "game_scoped_id": "1", "name": "Skyrim dependency", "game": {"domain_name": "skyrim"}}}}]},
            {"id": "2", "ranges": [{"target_mod_file": {"id": "11", "mod": {"id": "21", "game_scoped_id": "1", "name": "Enshrouded dependency", "game": {"domain_name": "enshrouded"}}}}]}
        ]})));
        let rec = record(1);
        let installed = InstalledMod { record: rec, info: InstalledInfo { version: "1".into(), version_gsid: "2".into(), file_name: "main".into(), in_library: true, latest_version: None, latest_gsid: None, update_available: false } };
        let requirements = cat.requirements("1", &[installed]);
        assert_eq!(requirements.len(), 2);
        assert!(!requirements[0].installed && requirements[0].other_game);
        assert!(requirements[1].installed && !requirements[1].other_game);
    }
}
