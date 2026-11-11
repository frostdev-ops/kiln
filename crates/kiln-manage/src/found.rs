//! Mods in the game folder that Kiln did not put there (docs/kiln/profiles.md "Mods in the game folder"). Players
//! extract mods into the game folder, as every mod page says; whether such a mod loads depends on the profile and on
//! each engine plugin's own rule. [`scan`] lists them for one game and one profile, each with a state the launcher can
//! say in plain words, and the engines' own loaders found next to the game. [`add`] takes one into the Library and the
//! profile. Nothing here moves or deletes the player's files.
//!
//! What counts: the entries of each engine's own folder (EML's `mods/` and `kiln/eml/mods/`, ShroudForge's mods folder,
//! `shrouded_engine_tweaks/mods/`, `greeneye/`, `mods/` for Flame, Shroudtopia and Enshwick, `shroudlink_mods/`,
//! `ember/mods/`) and Kiln's own drop-in folders (`kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods`). Skipped:
//! copies Kiln placed (`.kiln-library.json`), synced (`.kiln-source.json`) or that kiln-enshwick manages
//! (`.kiln-managed`), Kiln's own plugin DLLs (the install receipt, `kiln-*.dll`), Enshwick's loader folder
//! `mods/enshwick/`, hidden entries.
//!
//! The scan runs when a page opens, so it stays cheap: one listing per folder (at most [`MAX_ENTRIES`] entries), the
//! Library's own detection per entry (`library::detect`: manifests, and the DLLs directly in a mod folder), no hashing,
//! no zip reading, no DLL over [`MAX_DLL`] read. A missing folder is empty; an unreadable folder or entry is listed in
//! `unreadable`, never an error.

use crate::engines::Engine;
use crate::game::GameInstall;
use crate::library::{DetectKind, Detected, LibraryRef};
use crate::manifest::ModKind;
use crate::paths::DataDirs;
use crate::profile::{Profile, ProfileMod};
use crate::quick::{AddReport, AddedMod};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Component, Path, PathBuf};

/// Entries read per folder; more are left out and `truncated` says so.
pub const MAX_ENTRIES: usize = 512;
/// A DLL larger than this is not read to tell what it is.
pub const MAX_DLL: u64 = 64 * 1024 * 1024;
/// chain.ini `[chain]` entries looked at for engines' loaders.
const MAX_CHAIN: usize = 16;
/// kiln-enshwick's marker in the copies it manages in `<game>/mods/<id>/` (docs/kiln/engines/enshwick.md).
const ENSHWICK_MANAGED: &str = ".kiln-managed";

/// Whether a mod in the game folder loads with the profile.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FoundState {
    /// Loads at the next start with this profile (`via` says through what).
    Loads,
    /// Does not load with this profile; `reason` says why, `fix` what helps.
    Off,
    /// The profile has the same mod: the profile's copy wins.
    InProfile,
    /// Nothing Kiln can run (`reason` is the Library detection's).
    CannotRun,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FoundVia {
    /// Kiln's plugin for the kind reads the folder.
    Kiln,
    /// The engine's own loader is in the game folder and runs its folder itself (Kiln's plugin steps aside).
    OwnLoader,
}

/// The one action that helps a mod that does not load.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FoundFix {
    /// Switch on the profile's drop-in folder of the mod's kind (`kind`).
    Dropins,
    /// Import the mod into the Library and add it to the profile ([`add`]).
    Add,
}

/// One entry of an engine's folder.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FoundMod {
    /// The Library kind it was detected as (None: no mod kind, or a folder holding several mods).
    pub kind: Option<ModKind>,
    /// The name a player sees (the manifest's name, else the file or folder name).
    pub name: String,
    pub id: Option<String>,
    pub version: Option<String>,
    /// Relative to the game folder, with `/` (absolute only for a ShroudForge mods folder outside the game).
    pub path: String,
    pub state: FoundState,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub via: Option<FoundVia>,
    /// One plain sentence: why it loads or not.
    pub reason: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fix: Option<FoundFix>,
    /// InProfile: the profile entry's id.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile_mod: Option<String>,
}

/// An engine's own loader found next to the game.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnLoader {
    pub engine: Engine,
    /// The engine's name ("EML (Enshrouded Mod Loader)").
    pub name: String,
    /// The kinds whose game-folder mods it runs itself.
    pub kinds: Vec<ModKind>,
    /// Its file, relative to the game (`dbghelp.dll`, `kiln/chain/eml-dinput8.dll`).
    pub file: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Unreadable {
    pub path: String,
    pub reason: String,
}

/// [`scan`]'s answer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FoundReport {
    pub game_id: String,
    pub profile_id: String,
    /// Kiln's runtime is in this game (its receipt or its dinput8.dll). Without it only the engines' own loaders run.
    pub kiln_installed: bool,
    pub mods: Vec<FoundMod>,
    pub loaders: Vec<OwnLoader>,
    pub unreadable: Vec<Unreadable>,
    /// A folder had more than [`MAX_ENTRIES`] entries.
    pub truncated: bool,
}

impl FoundReport {
    /// The mods that do not load with this profile (what Play counts).
    pub fn off(&self) -> usize {
        self.mods.iter().filter(|m| m.state == FoundState::Off).count()
    }
}

// ---- where each kind's plugin looks ----

/// Which files of a folder are entries (folders always are).
#[derive(Clone, Copy, PartialEq, Eq)]
struct Files {
    dll: bool,
    zip: bool,
}

struct Root {
    /// As shown (relative to the game, `/`).
    rel: String,
    abs: PathBuf,
    /// The kinds whose plugin reads this folder.
    kinds: Vec<ModKind>,
    files: Files,
    /// Folders are entries (kiln/plugins: only DLLs).
    folders: bool,
}

fn rel_of(game: &Path, p: &Path) -> String {
    match p.strip_prefix(game) {
        Ok(r) => r.to_string_lossy().replace('\\', "/"),
        Err(_) => p.display().to_string(),
    }
}

fn roots(game: &Path) -> Vec<Root> {
    use ModKind as K;
    let none = Files { dll: false, zip: false };
    let mut v = vec![
        Root { rel: String::new(), abs: game.join(crate::paths::game::DROPIN_KILN), kinds: vec![K::Kiln], files: none, folders: true },
        Root { rel: String::new(), abs: game.join(crate::paths::game::DROPIN_NATIVE), kinds: vec![K::Native], files: none, folders: true },
        Root { rel: String::new(), abs: game.join(crate::paths::game::DROPIN_PLUGIN), kinds: vec![K::Plugin], files: Files { dll: true, zip: false }, folders: false },
        Root { rel: String::new(), abs: game.join(crate::paths::game::DROPIN_EMF), kinds: vec![K::Emf], files: Files { dll: true, zip: false }, folders: true },
        Root { rel: String::new(), abs: game.join("kiln/eml/mods"), kinds: vec![K::Eml], files: Files { dll: false, zip: true }, folders: true },
        Root { rel: String::new(), abs: game.join("mods"), kinds: vec![K::Eml, K::Xhl, K::Flame, K::Shroudtopia, K::Enshwick],
            files: Files { dll: true, zip: true }, folders: true },
        Root { rel: String::new(), abs: game.join(crate::engine_mods::SET_MODS), kinds: vec![K::Set], files: none, folders: true },
        Root { rel: String::new(), abs: game.join("greeneye"), kinds: vec![K::Greeneye], files: none, folders: true },
        Root { rel: String::new(), abs: game.join(crate::engine_mods::SHROUDLINK_MODS), kinds: vec![K::Shroudlink],
            files: Files { dll: true, zip: false }, folders: true },
        Root { rel: String::new(), abs: game.join(crate::engine_mods::EMBER_MODS), kinds: vec![K::Ember], files: none, folders: true },
    ];
    // ShroudForge's mods folder is `mods` unless its config names another
    let sf = crate::shroudforge::mods_dir(game);
    let same = |a: &Path, b: &Path| a == b || (a.canonicalize().ok().is_some() && a.canonicalize().ok() == b.canonicalize().ok());
    match v.iter_mut().find(|r| same(&r.abs, &sf)) {
        Some(r) => r.kinds.push(K::ShroudForge),
        None => v.push(Root { rel: String::new(), abs: sf, kinds: vec![K::ShroudForge], files: none, folders: true }),
    }
    for r in &mut v {
        r.rel = rel_of(game, &r.abs);
    }
    v
}

/// The folders a kind's plugin reads, for a sentence.
fn folders_of(kind: ModKind) -> &'static str {
    match kind {
        ModKind::Kiln => "kiln/mods",
        ModKind::Native => "kiln/native",
        ModKind::Plugin => "kiln/plugins",
        ModKind::Emf => "emf/mods",
        ModKind::Eml => "mods and kiln/eml/mods",
        ModKind::Xhl => "mods (only the separately installed XHL-Core reads these companions)",
        ModKind::ShroudForge => "ShroudForge's mods folder",
        ModKind::Set => "shrouded_engine_tweaks/mods",
        ModKind::Greeneye => "greeneye",
        ModKind::Flame | ModKind::Shroudtopia | ModKind::Enshwick => "mods",
        ModKind::Shroudlink => "shroudlink_mods",
        ModKind::Ember => "ember/mods",
    }
}

/// The kind's short name, for a sentence ("EML", "SET"; launcher/src/api/kinds.ts KIND_SHORT).
fn short(kind: ModKind) -> &'static str {
    match kind {
        ModKind::Eml => "EML",
        ModKind::Set => "SET",
        ModKind::Greeneye => "Greeneye",
        ModKind::Ember => "Ember",
        k => k.label(),
    }
}

// ---- the engines' own loaders ----

/// The kinds an engine's own loader runs from the game folder. ShroudForge's loader also runs EML packages (and carries
/// EML's marker, so kiln-eml steps aside for it too).
fn loader_kinds(e: Engine) -> Vec<ModKind> {
    use ModKind as K;
    match e {
        Engine::Eml => vec![K::Eml],
        Engine::ShroudForge => vec![K::ShroudForge, K::Eml],
        Engine::Emf => vec![K::Emf],
        Engine::Set => vec![K::Set],
        Engine::Enshwick => vec![K::Enshwick],
        Engine::Greeneye => vec![K::Greeneye],
        Engine::Shroudtopia => vec![K::Shroudtopia],
        Engine::Flame => vec![K::Flame],
        Engine::Shroudlink => vec![K::Shroudlink],
        Engine::Ember => vec![K::Ember],
        _ => vec![],
    }
}

/// The engines' loaders under a proxy name next to the game (`engines::scan_proxies`) and the ones Kiln's proxy chains
/// from `kiln/chain.ini [chain]`. Also: is Kiln's own proxy there.
fn own_loaders(game: &Path) -> (Vec<OwnLoader>, bool) {
    let mut out: Vec<OwnLoader> = Vec::new();
    let mut kiln = false;
    let mut push = |e: Engine, file: String| {
        let kinds = loader_kinds(e);
        if !kinds.is_empty() && !out.iter().any(|l| l.engine == e && l.file.eq_ignore_ascii_case(&file)) {
            out.push(OwnLoader { engine: e, name: e.name().to_string(), kinds, file });
        }
    };
    // shroudlink's client addon alone (no shroudlink_core.dll for it to start) runs no shroudlink mods
    let addon_only = |e: Engine, file: &Path| e == Engine::Shroudlink && crate::engines::shroudlink_addon_at(file)
        && !game.join("shroudlink_core.dll").is_file();
    for slot in crate::engines::scan_proxies(game) {
        match Engine::of_proxy(slot.class.kind) {
            Some(Engine::Kiln) => kiln = true,
            Some(e) if addon_only(e, &game.join(&slot.name)) => {}
            Some(e) => push(e, slot.name),
            None => {}
        }
    }
    if let Ok(ini) = crate::chain::read(game) {
        for c in ini.chain.iter().filter(|c| c.relative && c.status == crate::chain::EntryStatus::Ok).take(MAX_CHAIN) {
            let rel = c.path.replace('\\', "/");
            if Path::new(&rel).components().any(|x| !matches!(x, Component::Normal(_))) {
                continue;
            }
            let file = game.join(crate::paths::game::KILN).join(&rel);
            if let Ok(class) = crate::pe::classify_proxy(&file)
                && let Some(e) = Engine::of_proxy(class.kind).filter(|e| *e != Engine::Kiln)
                && !addon_only(e, &file)
            {
                push(e, format!("kiln/{rel}"));
            }
        }
    }
    (out, kiln)
}

// ---- each plugin's rule ----

/// Does the kind's plugin read its own folder with this profile, and why (a sentence without the "Loads"/"Does not
/// load" lead). The rules of [`crate::profile::Dropins`].
fn folder_read(kind: ModKind, p: &Profile) -> (bool, String) {
    let file = p.dropins_file();
    let label = short(kind);
    let on = || (true, format!("{label} drop-ins are on in this profile."));
    let off = || (false, format!("{label} drop-ins are off in this profile."));
    match kind {
        ModKind::Set => (true, "kiln-set always reads shrouded_engine_tweaks/mods.".to_string()),
        _ if file.names(kind) => on(),
        // kiln-shroudforge hosts ShroudForge, which runs its whole mods folder, for an enabled profile entry too
        ModKind::ShroudForge if p.mods.shroudforge.iter().any(|m| m.enabled) =>
            (true, "This profile has a ShroudForge mod, so ShroudForge runs its whole mods folder.".to_string()),
        // kiln-enshwick starts Enshwick's loader for an enabled profile entry; the loader then runs every folder
        ModKind::Enshwick if p.mods.enshwick.iter().any(|m| m.enabled) =>
            (true, "This profile has an Enshwick mod, so Enshwick's loader runs every folder in mods.".to_string()),
        _ => off(),
    }
}

/// The profile with `kind`'s drop-in folder switched on (what the launcher's switch saves).
pub fn with_dropin(p: &Profile, kind: ModKind) -> Profile {
    let mut q = p.clone();
    let mut kinds = p.dropin_kinds();
    if !kinds.contains(&kind) {
        kinds.push(kind);
    }
    q.dropins = ModKind::ALL.into_iter().filter(|k| *k != ModKind::Xhl && kinds.contains(k)).collect();
    q.dropins_engines = true;
    q
}

// ---- the profile's own copy ----

/// The profile entry of `kind` that is the same mod as the folder at `abs` (detected id `id`): the same id (case
/// ignored, or as the Library would name it), or a Library copy imported from that very path.
fn profile_entry<'a>(dirs: &DataDirs, p: &'a Profile, kind: ModKind, id: Option<&str>, abs: &Path) -> Option<&'a ProfileMod> {
    let list = p.mods.list(kind);
    if let Some(id) = id {
        let lib_id = crate::library::id_from_name(id);
        if let Some(m) = list.iter().find(|m| m.id.eq_ignore_ascii_case(id) || m.id == lib_id) {
            return Some(m);
        }
    }
    let from = abs.display().to_string();
    list.iter().find(|m| {
        let r = LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        crate::library::record(dirs, &r).ok().and_then(|rec| rec.source).is_some_and(|s| s.from == from)
    })
}

// ---- one entry ----

fn kiln_owned(dir: &Path) -> bool {
    [crate::engine_mods::MARKER, crate::paths::game::SOURCE_MARKER, ENSHWICK_MANAGED, crate::paths::game::DISABLED_MARKER]
        .iter()
        .any(|m| std::fs::symlink_metadata(dir.join(m)).is_ok())
}

/// A DLL directly in `dir` (or `dir` itself) too large to read for detection.
fn too_large(path: &Path) -> bool {
    let big = |p: &Path| std::fs::metadata(p).is_ok_and(|m| m.len() > MAX_DLL);
    if path.is_file() {
        return big(path);
    }
    std::fs::read_dir(path).into_iter().flatten().flatten().take(MAX_ENTRIES).any(|e| {
        let p = e.path();
        p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")) && big(&p)
    })
}

struct Ctx<'a> {
    dirs: &'a DataDirs,
    game: &'a Path,
    profile: &'a Profile,
    loaders: &'a [OwnLoader],
    kiln: bool,
    /// Wine or Proton: a loader under a proxy name next to the game runs only with a DLL override for that name.
    wine: bool,
}

fn found(name: String, path: String, state: FoundState, reason: String) -> FoundMod {
    FoundMod { kind: None, name, id: None, version: None, path, state, via: None, reason, fix: None, profile_mod: None }
}

fn entry(cx: &Ctx, root: &Root, abs: &Path, file_name: &str, is_zip: bool) -> FoundMod {
    let path = rel_of(cx.game, abs);
    let shown = file_name.to_string();
    let d: Detected = if is_zip {
        // the zip is not opened here: EML's runner takes a zip in its folders as one mod
        let stem = Path::new(file_name).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_else(|| shown.clone());
        Detected { kind: DetectKind::Eml, path: path.clone(), id: None, version: None, name: Some(stem),
            reason: "a .zip in EML's folder".into(), items: vec![], engine: Some(Engine::Eml) }
    } else if too_large(abs) {
        return found(shown, path, FoundState::CannotRun, format!("A DLL here is larger than {} MiB, too large to inspect.", MAX_DLL >> 20));
    } else {
        match crate::library::detect(abs) {
            Ok(d) => d,
            Err(e) => return found(shown, path, FoundState::CannotRun, e.message),
        }
    };
    let kind = ModKind::of_detect(d.kind);
    let name = d.name.clone().filter(|n| !n.trim().is_empty()).or_else(|| d.id.clone()).unwrap_or_else(|| shown.clone());
    let version = d.version.clone().filter(|v| !v.is_empty() && v != "0.0.0");
    let mut m = FoundMod { kind, name, id: d.id.clone(), version, path, state: FoundState::Off, via: None, reason: String::new(), fix: None, profile_mod: None };
    let Some(kind) = kind else {
        let importable = d.items.iter().filter(|i| i.kind.importable()).count();
        if d.kind == DetectKind::Unknown && importable > 0 {
            m.reason = format!("A folder holding {importable} mod{}. No loader looks inside it.", if importable == 1 { "" } else { "s" });
            m.fix = Some(FoundFix::Add);
        } else {
            m.state = FoundState::CannotRun;
            m.reason = sentence(&d.reason);
        }
        return m;
    };
    if !crate::engine_mods::runs_yet(kind) {
        m.state = FoundState::CannotRun;
        m.reason = "Kiln finds Ember mods but cannot run them yet: Ember has not published its mod ABI.".into();
        return m;
    }
    if !root.kinds.contains(&kind) {
        m.reason = format!("Nothing loads {} mods from {}: Kiln looks for them in {}.", short(kind), root.rel, folders_of(kind));
        m.fix = Some(FoundFix::Add);
        return m;
    }
    if kind == ModKind::Xhl {
        let host = crate::engine_mods::xhl_host(cx.game);
        m.reason = format!("{} This is a manually installed companion: XHL-Core may run it independently of profile and drop-in switches; Kiln does not execute it.", host.text);
        if host.available {
            m.state = FoundState::Loads;
            m.via = Some(FoundVia::OwnLoader);
        }
        return m;
    }
    if let Some(pm) = profile_entry(cx.dirs, cx.profile, kind, d.id.as_deref(), abs) {
        m.state = FoundState::InProfile;
        m.reason = if pm.enabled {
            "This profile has this mod too. The profile's copy loads.".into()
        } else {
            "This profile has this mod too, switched off. The profile's copy wins.".into()
        };
        m.profile_mod = Some(pm.id.clone());
        return m;
    }
    let loader = cx.loaders.iter().find(|l| l.kinds.contains(&kind));
    // A chained loader always runs. One under a proxy name always runs on Windows (the game imports those names);
    // under Wine and Proton only with a DLL override, which the launcher cannot see.
    let maybe = loader.filter(|l| cx.wine && !l.file.contains('/'));
    if let Some(l) = loader.filter(|_| maybe.is_none()) {
        m.state = FoundState::Loads;
        m.via = Some(FoundVia::OwnLoader);
        m.reason = format!("{}'s own loader ({}) is in the game folder and loads it itself.", short_engine(l), l.file);
        return m;
    }
    if !cx.kiln {
        m.reason = match maybe {
            Some(l) => format!("Kiln is not installed in this game. {}'s own loader ({}) loads it only when the game starts with a DLL override for that file.",
                short_engine(l), l.file),
            None => "Kiln is not installed in this game.".into(),
        };
        return m;
    }
    let (reads, why) = folder_read(kind, cx.profile);
    m.reason = match maybe {
        Some(l) if reads => format!("{why} {}'s own loader ({}) loads it instead when the game starts with a DLL override for that file.",
            short_engine(l), l.file),
        Some(l) => format!("{why} {}'s own loader ({}) is in the game folder, but on Linux it runs only when the game starts with a DLL override for that file.",
            short_engine(l), l.file),
        None => why,
    };
    if reads {
        m.state = FoundState::Loads;
        m.via = Some(FoundVia::Kiln);
    } else if folder_read(kind, &with_dropin(cx.profile, kind)).0 {
        m.fix = Some(FoundFix::Dropins);
    } else {
        m.fix = Some(FoundFix::Add);
    }
    m
}

fn short_engine(l: &OwnLoader) -> &'static str {
    match l.engine {
        Engine::Eml => "EML",
        e => crate::engines::info(e).name,
    }
}

/// The detection's reason as a sentence (capital, final period).
fn sentence(s: &str) -> String {
    let s = s.trim();
    let mut c = s.chars();
    let mut out = match c.next() {
        Some(f) => f.to_uppercase().collect::<String>() + c.as_str(),
        None => return "Not a mod Kiln knows.".into(),
    };
    if !out.ends_with('.') {
        out.push('.');
    }
    out
}

// ---- the scan ----

/// Every mod in the game folder that Kiln did not put there, and whether it loads with `profile`.
pub fn scan(dirs: &DataDirs, install: &GameInstall, profile: &Profile) -> FoundReport {
    let game = install.path.as_path();
    let (loaders, kiln_proxy) = own_loaders(game);
    let receipt = crate::install::read_receipt(game).ok().flatten();
    let kiln_installed = kiln_proxy || receipt.is_some();
    // Kiln's own files in kiln/plugins (kiln-*.dll, and anything its receipt lists)
    let shipped: Vec<String> = receipt.iter().flat_map(|r| r.files.iter().map(|f| f.path.replace('\\', "/").to_ascii_lowercase())).collect();
    let cx = Ctx { dirs, game, profile, loaders: &loaders, kiln: kiln_installed, wine: install.platform != crate::game::Platform::Windows };
    let mut rep = FoundReport {
        game_id: install.id.clone(),
        profile_id: profile.id.clone(),
        kiln_installed,
        mods: Vec::new(),
        loaders: loaders.clone(),
        unreadable: Vec::new(),
        truncated: false,
    };
    let mut seen: Vec<PathBuf> = Vec::new();
    for root in roots(game) {
        let rd = match std::fs::read_dir(&root.abs) {
            Ok(rd) => rd,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound || e.kind() == std::io::ErrorKind::NotADirectory => continue,
            Err(e) => {
                rep.unreadable.push(Unreadable { path: root.rel.clone(), reason: e.to_string() });
                continue;
            }
        };
        let mut names: Vec<(String, PathBuf, std::fs::FileType)> = Vec::new();
        for e in rd {
            if names.len() >= MAX_ENTRIES {
                rep.truncated = true;
                break;
            }
            let e = match e {
                Ok(e) => e,
                Err(err) => {
                    rep.unreadable.push(Unreadable { path: root.rel.clone(), reason: err.to_string() });
                    continue;
                }
            };
            let name = e.file_name().to_string_lossy().into_owned();
            match e.file_type() {
                Ok(t) => names.push((name, e.path(), t)),
                Err(err) => rep.unreadable.push(Unreadable { path: format!("{}/{name}", root.rel), reason: err.to_string() }),
            }
        }
        names.sort_by(|a, b| a.0.to_lowercase().cmp(&b.0.to_lowercase()));
        for (name, abs, t) in names {
            if name.starts_with('.') || name.eq_ignore_ascii_case("__MACOSX") || seen.contains(&abs) {
                continue;
            }
            let lower = name.to_ascii_lowercase();
            let rel = rel_of(game, &abs);
            if t.is_symlink() {
                if root.folders || lower.ends_with(".dll") {
                    rep.mods.push(found(name, rel, FoundState::CannotRun, "A link. The loaders do not follow links in the game folder.".into()));
                }
                continue;
            }
            let is_zip = t.is_file() && lower.ends_with(".zip");
            if t.is_dir() {
                if !root.folders || kiln_owned(&abs) {
                    continue;
                }
                // Enshwick's loader keeps its own files in mods/enshwick/: never a mod
                if root.kinds.contains(&ModKind::Enshwick) && lower == crate::engine_mods::ENSHWICK_LOADER_FOLDER {
                    continue;
                }
            } else if t.is_file() {
                let dll = lower.ends_with(".dll");
                if !((dll && root.files.dll) || (is_zip && root.files.zip)) {
                    continue;
                }
                if root.kinds.contains(&ModKind::Plugin)
                    && (lower.starts_with("kiln-") || shipped.iter().any(|s| *s == rel.to_ascii_lowercase()))
                {
                    continue;
                }
            } else {
                continue;
            }
            seen.push(abs.clone());
            rep.mods.push(entry(&cx, &root, &abs, &name, is_zip));
        }
    }
    rep
}

/// [`scan`] by ids (the launcher's command).
pub fn scan_ids(dirs: &DataDirs, install: &GameInstall, profile_id: &str) -> Result<FoundReport> {
    let p = crate::profile::get(dirs, profile_id)?;
    Ok(scan(dirs, install, &p))
}

// ---- add ----

/// The found entry's host path; refuses a path that is not one [`scan`] lists now.
fn found_path(dirs: &DataDirs, install: &GameInstall, profile: &Profile, rel: &str) -> Result<PathBuf> {
    let rep = scan(dirs, install, profile);
    if !rep.mods.iter().any(|m| m.path == rel) {
        return Err(Error::not_found(format!("{rel} is not a mod in the game folder (any more)")));
    }
    let p = Path::new(rel);
    if p.is_absolute() {
        return Ok(p.to_path_buf()); // ShroudForge's mods folder outside the game: scan listed it
    }
    if p.components().any(|c| !matches!(c, Component::Normal(_))) {
        return Err(Error::invalid(format!("{rel}: not a path inside the game folder")));
    }
    Ok(install.path.join(p))
}

/// "Add to this profile": imports the found mod at `rel` (a [`FoundMod::path`]) into the Library and adds it to the
/// profile, enabled. When the profile is the one active in this game, this is `quick::add` (which also writes the
/// game's profile.json); otherwise the Library import and a profile save. The player's folder stays where it is. What
/// the Library refused is in `import.skipped` with its reason.
pub fn add(dirs: &DataDirs, install: &GameInstall, profile_id: &str, rel: &str, progress: &mut dyn Progress) -> Result<AddReport> {
    let profile = crate::profile::get(dirs, profile_id)?;
    let path = found_path(dirs, install, &profile, rel)?;
    let active = crate::profile::read_game_profile(&install.path).ok().flatten().is_some_and(|f| f.id == profile.id);
    if active {
        return crate::quick::add(dirs, install, &[path], progress);
    }
    let mut report = AddReport { import: crate::library::import(dirs, &[path], progress)?, ..AddReport::default() };
    if progress.canceled() {
        return Err(Error::new(ErrorCode::Canceled, "canceled"));
    }
    let mut refs: Vec<LibraryRef> = Vec::new();
    for r in report.import.imported.iter().chain(&report.import.existing) {
        match refs.iter().position(|o| o.kind == r.kind && o.id == r.id) {
            Some(i) if crate::bundle::compare_versions(&r.version, &refs[i].version).is_gt() => refs[i] = r.clone(),
            Some(_) => {}
            None => refs.push(r.clone()),
        }
    }
    report.profile_id = Some(profile.id.clone());
    report.profile_name = Some(profile.name.clone());
    if refs.is_empty() {
        return Ok(report);
    }
    let mut p = crate::profile::get(dirs, profile_id)?; // the import may take a while: the current generation
    let named = |r: &LibraryRef| -> AddedMod {
        let name = crate::library::record(dirs, r).ok().and_then(|x| x.name).filter(|n| !n.trim().is_empty()).unwrap_or_else(|| r.id.clone());
        AddedMod { kind: r.kind, id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), name }
    };
    // the rules quick::add puts mods into a profile with (one copy of a proxy DLL, XHL-Core builds, notices)
    let (changes, already, notices) = crate::quick::merge(dirs, &mut p.mods, &refs);
    report.already = already.iter().map(named).collect();
    report.added = changes.iter().filter(|c| c.after.enabled)
        .map(|c| named(&LibraryRef { kind: c.kind, id: c.after.id.clone(), version: c.after.version.clone(), hash: c.after.hash.clone() })).collect();
    report.changes = changes;
    report.notices = notices;
    if !report.changes.is_empty() {
        crate::profile::save(dirs, &p)?;
    }
    progress.report(Some(1.0), &format!("{} added to {}", report.added.len(), profile.name));
    Ok(report)
}

#[cfg(test)]
mod tests;
