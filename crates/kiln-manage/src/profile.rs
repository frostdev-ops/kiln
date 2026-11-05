//! Profiles. The launcher's definition is `<data>/profiles/<pid>/meta.json` ([`Profile`]); activating it for a game
//! renders `<game>/kiln/profile.json` ([`ProfileFile`], schema/profile.schema.json) with game-side paths, writes
//! chain.ini [profile] (dllmain native mods), and in copy mode first copies the mods to `<game>/kiln/profiles/<pid>/`.
//! Steam's Play button then boots the last activated profile; a missing profile.json is the legacy layout.

use crate::game::{GameInstall, PathMode};
use crate::manifest::{ModKind, RunSide};
use crate::paths::DataDirs;
use crate::resolver::Resolution;
use crate::{Error, Result};
use serde::{Deserialize, Serialize};

// ---- game side: <game>/kiln/profile.json ----

/// `dropins`: true/false, or a list of kinds.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Dropins {
    All(bool),
    Kinds(Vec<ModKind>),
}

impl Default for Dropins {
    fn default() -> Self {
        Dropins::All(false)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackRef {
    pub url: String,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub size: Option<u64>,
}

/// The server a profile is bound to (modpack profiles "Server: <name>").
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BoundServer {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub address: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modpack: Option<ModpackRef>,
}

fn yes() -> bool {
    true
}

fn is_true(b: &bool) -> bool {
    *b
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderMod {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    /// Game-side folder path.
    pub path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hash: Option<String>,
    #[serde(default = "yes", skip_serializing_if = "is_true")]
    pub enabled: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginMod {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    /// Game-side DLL path.
    pub path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hash: Option<String>,
    #[serde(default = "yes", skip_serializing_if = "is_true")]
    pub enabled: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileFileMods {
    #[serde(default)]
    pub kiln: Vec<FolderMod>,
    #[serde(default)]
    pub native: Vec<FolderMod>,
    #[serde(default)]
    pub emf: Vec<FolderMod>,
    #[serde(default)]
    pub plugin: Vec<PluginMod>,
}

/// `<game>/kiln/profile.json` (and `kiln/profile.once.json`), format 1.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileFile {
    #[serde(default, rename = "$schema", skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    pub format: u32,
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub generation: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub written: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub writer: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path_mode: Option<PathMode>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub config_dir: Option<String>,
    #[serde(default)]
    pub data_dir: Option<String>,
    #[serde(default)]
    pub dropins: Dropins,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<String>,
    #[serde(default)]
    pub server: Option<BoundServer>,
    pub mods: ProfileFileMods,
}

// ---- launcher side: <data>/profiles/<pid>/meta.json ----

/// A library reference inside a profile.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileMod {
    pub id: String,
    pub version: String,
    pub hash: String,
    #[serde(default = "yes")]
    pub enabled: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileMods {
    #[serde(default)]
    pub kiln: Vec<ProfileMod>,
    #[serde(default)]
    pub native: Vec<ProfileMod>,
    #[serde(default)]
    pub plugin: Vec<ProfileMod>,
    #[serde(default)]
    pub emf: Vec<ProfileMod>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DataDirMode {
    /// kiln/data (default): mod data shared by every profile of the game.
    #[default]
    Shared,
    /// `<data>/profiles/<pid>/data`
    Profile,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    pub format: u32,
    pub id: String,
    pub name: String,
    pub created: String,
    pub updated: String,
    /// Bumped by every save; `save` refuses (Conflict) a profile whose generation is not the stored one.
    pub generation: u64,
    #[serde(default)]
    pub notes: String,
    pub mods: ProfileMods,
    #[serde(default)]
    pub dropins: Vec<ModKind>,
    #[serde(default)]
    pub data_dir: DataDirMode,
    #[serde(default)]
    pub server: Option<BoundServer>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileCounts {
    pub kiln: u32,
    pub native: u32,
    pub plugin: u32,
    pub emf: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileSummary {
    pub id: String,
    pub name: String,
    pub updated: String,
    pub counts: ProfileCounts,
    pub server: Option<BoundServer>,
    /// Game install ids this profile is active on.
    pub active: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Activation {
    pub game_id: String,
    pub profile_id: String,
    pub generation: u64,
    pub path_mode: PathMode,
    /// Host path of the profile.json written.
    pub profile_json: String,
    /// Copy mode: folders copied.
    pub copied: u32,
    pub fingerprint: String,
    pub activated: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativePlanEntry {
    pub id: String,
    pub version: String,
    pub phase: crate::manifest::NativePhase,
    pub enabled: bool,
    pub reason: Option<String>,
}

/// What would load (resolver over the profile's Kiln mods for a side, plus the native order).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvePreview {
    pub side: RunSide,
    pub kiln: Resolution,
    pub native: Vec<NativePlanEntry>,
    pub warnings: Vec<String>,
}

/// Kiln.Mods.ApiVersion of this Kiln (managed/Kiln.Api/Mods.cs; a test keeps them equal).
pub const KILN_API: &str = "0.2.0";
pub const META: &str = "meta.json";

use crate::library::LibraryRef;
use crate::manifest::{Dependency, Incompatibility, IncompatibleSpec, KilnManifest, NativeManifest, NativePhase};
use crate::resolver::{Candidate, Environment, ModState, Rejection};
use crate::util::{io_at, now_rfc3339, ts_compact};
use crate::ErrorCode;
use std::path::{Path, PathBuf};

fn meta_path(dirs: &DataDirs, id: &str) -> PathBuf {
    dirs.profile(id).join(META)
}

/// Profile ids: `^[a-z0-9][a-z0-9-]{0,47}$`.
pub fn is_profile_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 48
        && id.bytes().next().is_some_and(|b| b.is_ascii_lowercase() || b.is_ascii_digit())
        && id.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

fn check_id(id: &str) -> Result<()> {
    if is_profile_id(id) { Ok(()) } else { Err(Error::invalid(format!("not a profile id: {id:?} (a-z, 0-9, -; at most 48)"))) }
}

/// `Server: Ember Isles` -> `server-ember-isles`.
pub fn id_from_name(name: &str) -> String {
    let mut s = String::new();
    for c in name.to_lowercase().chars() {
        if c.is_ascii_alphanumeric() {
            s.push(c);
        } else if !s.ends_with('-') {
            s.push('-');
        }
    }
    let s: String = s.trim_matches('-').chars().take(44).collect();
    let s = s.trim_matches('-').to_string();
    if s.is_empty() { "profile".into() } else { s }
}

fn list_of(p: &ProfileMods, k: ModKind) -> &Vec<ProfileMod> {
    match k {
        ModKind::Kiln => &p.kiln,
        ModKind::Native => &p.native,
        ModKind::Plugin => &p.plugin,
        ModKind::Emf => &p.emf,
    }
}

fn lref(kind: ModKind, m: &ProfileMod) -> LibraryRef {
    LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() }
}

fn check_profile(p: &Profile) -> Result<()> {
    check_id(&p.id)?;
    if p.format > 1 {
        return Err(Error::new(ErrorCode::Unsupported, format!("profile format {} (this Kiln reads format 1)", p.format)));
    }
    if p.name.trim().is_empty() {
        return Err(Error::invalid("a profile needs a name"));
    }
    let mut problems = Vec::new();
    for k in ModKind::ALL {
        let mut seen = std::collections::BTreeSet::new();
        for m in list_of(&p.mods, k) {
            if !crate::manifest::is_mod_id(&m.id) {
                problems.push(format!("{} mod id {:?} is not valid", k.as_str(), m.id));
            }
            if crate::hash::hash8(&m.hash).is_none() {
                problems.push(format!("{} {}: bad hash {:?}", k.as_str(), m.id, m.hash));
            }
            if !seen.insert(m.id.clone()) {
                problems.push(format!("{} {} is listed twice", k.as_str(), m.id));
            }
        }
    }
    if !problems.is_empty() {
        return Err(Error::new(ErrorCode::Schema, format!("the profile breaks the format's rules: {}", problems.join("; "))).with_detail(problems));
    }
    Ok(())
}

pub fn get(dirs: &DataDirs, id: &str) -> Result<Profile> {
    check_id(id)?;
    let p: Profile = crate::util::read_json(&meta_path(dirs, id))?.ok_or_else(|| Error::not_found(format!("no profile {id}")))?;
    if p.id != id {
        return Err(Error::new(ErrorCode::Schema, format!("profiles/{id}/meta.json says id {}", p.id)));
    }
    Ok(p)
}

fn counts(p: &Profile) -> ProfileCounts {
    let n = |k| list_of(&p.mods, k).len() as u32;
    ProfileCounts { kiln: n(ModKind::Kiln), native: n(ModKind::Native), plugin: n(ModKind::Plugin), emf: n(ModKind::Emf) }
}

pub fn list(dirs: &DataDirs) -> Result<Vec<ProfileSummary>> {
    let installs = crate::game::known(dirs).unwrap_or_default();
    let active: Vec<(String, String)> = installs
        .iter()
        .filter_map(|g| read_game_profile(&g.path).ok().flatten().map(|f| (f.id, g.id.clone())))
        .collect();
    let mut out = Vec::new();
    for e in std::fs::read_dir(dirs.profiles()).into_iter().flatten().flatten() {
        let id = e.file_name().to_string_lossy().into_owned();
        let Ok(p) = get(dirs, &id) else { continue };
        out.push(ProfileSummary {
            counts: counts(&p),
            active: active.iter().filter(|(pid, _)| *pid == p.id).map(|(_, g)| g.clone()).collect(),
            id: p.id,
            name: p.name,
            updated: p.updated,
            server: p.server,
        });
    }
    out.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()).then(a.id.cmp(&b.id)));
    Ok(out)
}

/// A new profile (id derived from the name), optionally a copy of another.
pub fn create(dirs: &DataDirs, name: &str, copy_from: Option<&str>) -> Result<Profile> {
    let name = name.trim();
    if name.is_empty() {
        return Err(Error::invalid("a profile needs a name"));
    }
    let base = id_from_name(name);
    let mut id = base.clone();
    let mut n = 2;
    while dirs.profile(&id).exists() {
        let suffix = format!("-{n}");
        id = format!("{}{suffix}", &base[..base.len().min(48 - suffix.len())]);
        n += 1;
    }
    let now = now_rfc3339();
    let src = copy_from.map(|s| get(dirs, s)).transpose()?;
    let p = Profile {
        format: 1,
        id: id.clone(),
        name: name.to_string(),
        created: now.clone(),
        updated: now,
        generation: 1,
        notes: src.as_ref().map(|s| s.notes.clone()).unwrap_or_default(),
        mods: src.as_ref().map(|s| s.mods.clone()).unwrap_or_default(),
        dropins: src.as_ref().map(|s| s.dropins.clone()).unwrap_or_default(),
        data_dir: src.as_ref().map(|s| s.data_dir).unwrap_or_default(),
        server: src.as_ref().and_then(|s| s.server.clone()),
    };
    let dir = dirs.profile(&id);
    for d in ["config", "data"] {
        std::fs::create_dir_all(dir.join(d)).map_err(io_at(&dir, "creating"))?;
    }
    if let Some(s) = &src {
        let from = dirs.profile(&s.id).join("config");
        if from.is_dir() {
            crate::util::copy_tree(&from, &dir.join("config"))?;
        }
    }
    crate::util::write_json_atomic(&meta_path(dirs, &id), &p)?;
    Ok(p)
}

/// Saves meta.json (generation + 1). Conflict when `profile.generation` is not the stored generation.
pub fn save(dirs: &DataDirs, profile: &Profile) -> Result<Profile> {
    check_profile(profile)?;
    let stored = get(dirs, &profile.id)?;
    if stored.generation != profile.generation {
        return Err(Error::new(
            ErrorCode::Conflict,
            format!("profile {} changed meanwhile (generation {} on disk, {} here); reload it", profile.id, stored.generation, profile.generation),
        ));
    }
    let mut p = profile.clone();
    p.generation += 1;
    p.created = stored.created;
    p.updated = now_rfc3339();
    crate::util::write_json_atomic(&meta_path(dirs, &p.id), &p)?;
    Ok(p)
}

/// Moves the profile folder to `<data>/backups/profiles/<id>-<ts>/`.
pub fn delete(dirs: &DataDirs, id: &str) -> Result<()> {
    get(dirs, id)?;
    let to = dirs.backups().join("profiles").join(format!("{id}-{}", ts_compact(crate::util::now_unix())));
    std::fs::create_dir_all(to.parent().expect("parent")).map_err(io_at(&to, "creating"))?;
    std::fs::rename(dirs.profile(id), &to).map_err(io_at(&to, "moving the profile to"))?;
    Ok(())
}

/// A native.json as the resolver's manifest (same rules: requires = dependency, after/before, incompatible, the
/// `kiln` range as a `kiln` dependency).
fn native_as_manifest(n: &NativeManifest) -> KilnManifest {
    let mut deps: Vec<Dependency> = n.requires.iter().map(|r| Dependency { id: r.clone(), range: "*".into(), optional: false }).collect();
    if let Some(k) = &n.kiln {
        deps.push(Dependency { id: crate::manifest::KILN_ID.into(), range: k.clone(), optional: false });
    }
    let incompatible = match &n.incompatible {
        IncompatibleSpec::Ids(v) => v.iter().map(|i| Incompatibility { id: i.clone(), range: "*".into() }).collect(),
        IncompatibleSpec::Ranges(m) => m.iter().map(|(i, r)| Incompatibility { id: i.clone(), range: r.clone() }).collect(),
    };
    KilnManifest {
        id: n.id.clone(),
        name: n.name.clone().unwrap_or_else(|| n.id.clone()),
        version: n.version.clone(),
        parsed_version: crate::semver::SemVersion::parse(&n.version).map(|v| v.to_string()).unwrap_or_else(|_| n.version.clone()),
        api_version: KILN_API.into(),
        entry_assembly: n.dlls.first().cloned().unwrap_or_default(),
        entry_type: None,
        side: n.side,
        clients: n.clients.unwrap_or(crate::manifest::Clients::default_for(n.side)),
        authors: Vec::new(),
        description: String::new(),
        license: None,
        url: None,
        dependencies: deps,
        load_before: n.before.clone(),
        load_after: n.after.clone(),
        incompatible,
        features: Vec::new(),
        exports: Vec::new(),
        advanced: false,
        tests: None,
        warnings: Vec::new(),
    }
}

fn environment(side: RunSide) -> Environment {
    Environment { kiln: crate::VERSION.into(), api: KILN_API.into(), side, unavailable_features: Vec::new(), loaded: Vec::new() }
}

pub fn resolve_preview(dirs: &DataDirs, profile: &Profile, side: RunSide) -> Result<ResolvePreview> {
    let mut warnings = Vec::new();
    // managed mods
    let mut candidates = Vec::new();
    let mut disabled = Vec::new();
    for m in &profile.mods.kiln {
        let folder = crate::library::path_of(dirs, &lref(ModKind::Kiln, m))?;
        let dir = folder.display().to_string();
        if !m.enabled {
            disabled.push(Rejection { id: m.id.clone(), dir, state: ModState::Disabled, reason: "disabled in the profile".into() });
            continue;
        }
        if !folder.is_dir() {
            warnings.push(format!("kiln mod {} {} is not in the library", m.id, m.version));
            candidates.push(Candidate { dir, manifest: None, error: Some(format!("{} {} is not in the library", m.id, m.version)) });
            continue;
        }
        match crate::manifest::read_kiln(&folder) {
            Ok(man) => candidates.push(Candidate { dir, manifest: Some(man), error: None }),
            Err(e) => candidates.push(Candidate { dir, manifest: None, error: Some(e.message) }),
        }
    }
    let mut kiln = crate::resolver::resolve(&candidates, &environment(side))?;
    kiln.rejected.extend(disabled);
    // native mods: the same rules over their native.json
    let mut ncands = Vec::new();
    let mut phases: std::collections::BTreeMap<String, (String, NativePhase)> = Default::default();
    let mut native = Vec::new();
    for m in &profile.mods.native {
        let folder = crate::library::path_of(dirs, &lref(ModKind::Native, m))?;
        let man = crate::manifest::read_native(&folder);
        let phase = man.as_ref().map(|n| n.phase).unwrap_or_default();
        if !m.enabled {
            native.push(NativePlanEntry { id: m.id.clone(), version: m.version.clone(), phase, enabled: false, reason: Some("disabled in the profile".into()) });
            continue;
        }
        match man {
            Ok(n) => {
                phases.insert(n.id.clone(), (n.version.clone(), n.phase));
                ncands.push(Candidate { dir: folder.display().to_string(), manifest: Some(native_as_manifest(&n)), error: None });
            }
            Err(e) => {
                warnings.push(format!("native mod {}: {}", m.id, e.message));
                native.push(NativePlanEntry { id: m.id.clone(), version: m.version.clone(), phase, enabled: false, reason: Some(e.message) });
            }
        }
    }
    let nres = crate::resolver::resolve(&ncands, &environment(side))?;
    let mut ordered: Vec<NativePlanEntry> = nres
        .order
        .iter()
        .map(|id| {
            let (version, phase) = phases.get(id).cloned().unwrap_or_default();
            NativePlanEntry { id: id.clone(), version, phase, enabled: true, reason: None }
        })
        .collect();
    for r in nres.rejected {
        let (version, phase) = phases.get(&r.id).cloned().unwrap_or_default();
        native.push(NativePlanEntry { id: r.id, version, phase, enabled: false, reason: Some(r.reason) });
    }
    ordered.extend(native);
    for k in [ModKind::Plugin, ModKind::Emf] {
        for m in list_of(&profile.mods, k) {
            let p = crate::library::path_of(dirs, &lref(k, m)).ok();
            if !p.as_ref().is_some_and(|p| p.exists()) {
                warnings.push(format!("{} {} {} is not in the library", k.as_str(), m.id, m.version));
            }
        }
    }
    Ok(ResolvePreview { side, kiln, native: ordered, warnings })
}

fn game_or_copy(host: &Path, install: &GameInstall, mode: PathMode, rel: impl FnOnce() -> String) -> Result<String> {
    match mode {
        PathMode::Copy => Ok(rel()),
        _ => {
            let mut g = install.clone();
            g.path_mode = mode;
            crate::winpath::to_game(host, &g)
        }
    }
}

fn copy_rel(pid: &str, kind: ModKind, id: &str) -> String {
    format!("{}/{pid}/{}/{id}", COPY_ROOT_REL, kind.as_str())
}

/// `<game>/kiln/profiles` relative to `<game>/kiln/`.
const COPY_ROOT_REL: &str = "profiles";

/// Renders the game-side file for an install (no writes): game paths, fingerprint, generation.
pub fn render(dirs: &DataDirs, profile: &Profile, install: &GameInstall, mode: PathMode) -> Result<ProfileFile> {
    let pid = &profile.id;
    let mut mods = ProfileFileMods::default();
    for k in [ModKind::Kiln, ModKind::Native, ModKind::Emf] {
        for m in list_of(&profile.mods, k) {
            let folder = crate::library::path_of(dirs, &lref(k, m))?;
            let path = game_or_copy(&folder, install, mode, || copy_rel(pid, k, &m.id))?;
            let fm = FolderMod { id: m.id.clone(), version: Some(m.version.clone()), path, hash: Some(m.hash.clone()), enabled: m.enabled };
            match k {
                ModKind::Kiln => mods.kiln.push(fm),
                ModKind::Native => mods.native.push(fm),
                _ => mods.emf.push(fm),
            }
        }
    }
    for m in &profile.mods.plugin {
        let dll = crate::library::path_of(dirs, &lref(ModKind::Plugin, m))?;
        let file = dll.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_default();
        let path = game_or_copy(&dll, install, mode, || format!("{}/{file}", copy_rel(pid, ModKind::Plugin, &m.id)))?;
        mods.plugin.push(PluginMod { id: Some(m.id.clone()), version: Some(m.version.clone()), path, hash: Some(m.hash.clone()), enabled: m.enabled });
    }
    let pdir = dirs.profile(pid);
    let config_dir = game_or_copy(&pdir.join("config"), install, mode, || format!("{COPY_ROOT_REL}/{pid}/config"))?;
    let data_dir = match profile.data_dir {
        DataDirMode::Shared => None,
        DataDirMode::Profile => Some(game_or_copy(&pdir.join("data"), install, mode, || format!("{COPY_ROOT_REL}/{pid}/data"))?),
    };
    let dropins = match profile.dropins.len() {
        0 => Dropins::All(false),
        _ if ModKind::ALL.iter().all(|k| profile.dropins.contains(k)) => Dropins::All(true),
        _ => Dropins::Kinds(profile.dropins.clone()),
    };
    Ok(ProfileFile {
        schema: None,
        format: 1,
        id: profile.id.clone(),
        name: Some(profile.name.clone()),
        generation: profile.generation,
        written: None,
        writer: Some(format!("kiln-manage {}", crate::VERSION)),
        path_mode: Some(mode),
        config_dir: Some(config_dir),
        data_dir,
        dropins,
        fingerprint: Some(fingerprint(profile)),
        server: profile.server.clone(),
        mods,
    })
}

pub fn read_game_profile(game_dir: &std::path::Path) -> Result<Option<ProfileFile>> {
    let p: Option<ProfileFile> = crate::util::read_json(&game_dir.join(crate::paths::game::PROFILE))?;
    if let Some(f) = &p
        && f.format > 1
    {
        return Err(Error::new(ErrorCode::Unsupported, format!("kiln/profile.json format {} (this Kiln reads format 1)", f.format)));
    }
    Ok(p)
}

fn refuse_running(install: &GameInstall) -> Result<()> {
    if let Some(p) = crate::proc::find_game(install)? {
        return Err(Error::new(ErrorCode::GameRunning, format!("{} is running (pid {}); quit it first", install.exe, p.pid))
            .with_detail(serde_json::json!({ "pid": p.pid })));
    }
    Ok(())
}

fn ensure_kiln(install: &GameInstall) -> Result<PathBuf> {
    let kiln = install.path.join(crate::paths::game::KILN);
    if !kiln.is_dir() {
        return Err(Error::not_found(format!("{} has no kiln/ folder: install Kiln first", install.path.display())));
    }
    Ok(kiln)
}

/// Copy mode: `<game>/kiln/profiles/<pid>/<kind>/<id>/` hold verified copies of the enabled mods; unchanged copies
/// are kept, copies of mods the profile no longer lists are removed. Returns the number of folders copied.
fn sync_copies(dirs: &DataDirs, profile: &Profile, install: &GameInstall) -> Result<u32> {
    let base = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id);
    let mut copied = 0;
    for k in ModKind::ALL {
        let kdir = base.join(k.as_str());
        let wanted: Vec<&ProfileMod> = list_of(&profile.mods, k).iter().filter(|m| m.enabled).collect();
        for e in std::fs::read_dir(&kdir).into_iter().flatten().flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            if !wanted.iter().any(|m| m.id == name) {
                let _ = std::fs::remove_dir_all(e.path());
            }
        }
        for m in wanted {
            let src = crate::library::path_of(dirs, &lref(k, m))?;
            let dst = kdir.join(&m.id);
            if k == ModKind::Plugin {
                let file = src.file_name().map(|f| f.to_owned()).unwrap_or_default();
                let target = dst.join(&file);
                if crate::hash::sha256_file(&target).is_ok_and(|(h, _)| h == m.hash) {
                    continue;
                }
                let _ = std::fs::remove_dir_all(&dst);
                std::fs::create_dir_all(&dst).map_err(io_at(&dst, "creating"))?;
                std::fs::copy(&src, &target).map_err(io_at(&target, "copying to"))?;
                if crate::hash::sha256_file(&target)?.0 != m.hash {
                    return Err(Error::new(ErrorCode::HashMismatch, format!("{}: the copy does not match {}", target.display(), m.hash)));
                }
            } else {
                if dst.is_dir() && crate::hash::dirhash(&dst).is_ok_and(|h| h == m.hash) {
                    continue;
                }
                let _ = std::fs::remove_dir_all(&dst);
                crate::util::copy_tree(&src, &dst)?;
                let h = crate::hash::dirhash(&dst)?;
                if h != m.hash {
                    return Err(Error::new(ErrorCode::HashMismatch, format!("{}: the copy has {h}, the library entry {}", dst.display(), m.hash)));
                }
            }
            copied += 1;
        }
    }
    Ok(copied)
}

/// Copy mode: the game-side config folder (`<game>/kiln/profiles/<pid>/config`, where Kiln writes) back into
/// `<data>/profiles/<pid>/config`. Called before an activation and after the game exits. Returns the files copied.
pub fn copy_back(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<u64> {
    let from = install.path.join(crate::paths::game::COPY_PROFILES).join(profile_id).join("config");
    if !from.is_dir() {
        return Ok(0);
    }
    crate::util::copy_tree(&from, &dirs.profile(profile_id).join("config"))
}

fn config_host_dir(dirs: &DataDirs, profile: &Profile, install: &GameInstall, mode: PathMode) -> PathBuf {
    match mode {
        PathMode::Copy => install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id).join("config"),
        _ => dirs.profile(&profile.id).join("config"),
    }
}

/// The profile's native switches into `<configDir>/native.json` (other mods' switches stay).
fn write_native_switches(config_dir: &Path, profile: &Profile) -> Result<()> {
    let mut st = crate::config::read_native_state(config_dir).unwrap_or_default();
    st.format = 1;
    for m in &profile.mods.native {
        st.mods.insert(m.id.clone(), crate::config::NativeModSwitch { enabled: Some(m.enabled) });
    }
    crate::config::write_native_state(config_dir, &st)
}

/// Game-side DLL paths of the enabled native mods with phase dllmain (chain.ini [profile] / once.ini).
fn dllmain_dlls(dirs: &DataDirs, profile: &Profile, file: &ProfileFile) -> Result<Vec<String>> {
    let mut out = Vec::new();
    for (m, fm) in profile.mods.native.iter().zip(&file.mods.native) {
        if !m.enabled {
            continue;
        }
        let folder = crate::library::path_of(dirs, &lref(ModKind::Native, m))?;
        let Ok(n) = crate::manifest::read_native(&folder) else { continue };
        if n.phase != NativePhase::Dllmain {
            continue;
        }
        for d in &n.dlls {
            // chain.ini paths use '\'; relative ones resolve against <game>/kiln/
            out.push(format!("{}\\{d}", fm.path.trim_end_matches(['\\', '/'])).replace('/', "\\"));
        }
    }
    Ok(out)
}

/// Prepares everything a profile file needs on the game side (path mode, copies, config dir, native.json) and
/// renders it. Shared by activation and play-once profiles.
fn prepare(dirs: &DataDirs, profile: &Profile, install: &GameInstall) -> Result<(ProfileFile, PathMode, u32)> {
    let mut mode = crate::game::path_mode(install, dirs)?;
    let file = match render(dirs, profile, install, mode) {
        Ok(f) => f,
        Err(e) if e.code == ErrorCode::Unsupported && mode == PathMode::Z => {
            mode = PathMode::Copy; // a path Windows cannot name: the whole profile is copied
            render(dirs, profile, install, mode)?
        }
        Err(e) => return Err(e),
    };
    let mut copied = 0;
    if mode == PathMode::Copy {
        copy_back(dirs, &profile.id, install)?;
        copied = sync_copies(dirs, profile, install)?;
        let game_cfg = config_host_dir(dirs, profile, install, mode);
        let _ = std::fs::remove_dir_all(&game_cfg);
        let data_cfg = dirs.profile(&profile.id).join("config");
        std::fs::create_dir_all(&data_cfg).map_err(io_at(&data_cfg, "creating"))?;
        crate::util::copy_tree(&data_cfg, &game_cfg)?;
        if profile.data_dir == DataDirMode::Profile {
            let d = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id).join("data");
            std::fs::create_dir_all(&d).map_err(io_at(&d, "creating"))?;
        }
    } else {
        for d in ["config", "data"] {
            let p = dirs.profile(&profile.id).join(d);
            std::fs::create_dir_all(&p).map_err(io_at(&p, "creating"))?;
        }
    }
    write_native_switches(&config_host_dir(dirs, profile, install, mode), profile)?;
    Ok((file, mode, copied))
}

fn activation_record(dirs: &DataDirs, install: &GameInstall) -> PathBuf {
    dirs.installs().join(format!("{}.active.json", install.id))
}

fn remove_if_there(p: &Path) -> Result<()> {
    match std::fs::remove_file(p) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(io_at(p, "removing")(e)),
    }
}

/// The activation flow (contracts.md): refuse while running, path mode, copy, chain.ini [profile], profile.json.
pub fn activate(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<Activation> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    let (mut file, mode, copied) = prepare(dirs, &profile, install)?;
    let prev = read_game_profile(&install.path).ok().flatten().map(|f| f.generation).unwrap_or(0);
    file.generation = prev + 1; // grows with every write of the file
    let now = now_rfc3339();
    file.written = Some(now.clone());
    crate::chain::write_profile_section(&install.path, &dllmain_dlls(dirs, &profile, &file)?)?;
    let pj = install.path.join(crate::paths::game::PROFILE);
    crate::util::write_json_atomic(&pj, &file)?;
    remove_if_there(&install.path.join(crate::paths::game::CHAIN_ONCE))?;
    remove_if_there(&install.path.join(crate::paths::game::PROFILE_ONCE))?;
    let a = Activation {
        game_id: install.id.clone(),
        profile_id: profile.id.clone(),
        generation: file.generation,
        path_mode: mode,
        profile_json: pj.display().to_string(),
        copied,
        fingerprint: file.fingerprint.clone().unwrap_or_default(),
        activated: now,
    };
    let _ = crate::util::write_json_atomic(&activation_record(dirs, install), &a);
    Ok(a)
}

/// Launch mode once: renders the profile into `kiln/profile.once.json` and its dllmain natives into
/// `kiln/chain/once.ini` (play-once mode=profile then boots it for one start).
pub fn write_once(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<ProfileFile> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    let (mut file, _, _) = prepare(dirs, &profile, install)?;
    file.written = Some(now_rfc3339());
    crate::chain::write_once(&install.path, &dllmain_dlls(dirs, &profile, &file)?)?;
    crate::util::write_json_atomic(&install.path.join(crate::paths::game::PROFILE_ONCE), &file)?;
    Ok(file)
}

/// Back to the legacy layout: profile.json and chain.ini [profile] removed (a copy is kept in kiln/backups/).
pub fn deactivate(dirs: &DataDirs, install: &GameInstall) -> Result<()> {
    refuse_running(install)?;
    let pj = install.path.join(crate::paths::game::PROFILE);
    if pj.is_file() {
        let to = install.path.join(crate::paths::game::BACKUPS).join(format!("profile-{}.json", ts_compact(crate::util::now_unix())));
        std::fs::create_dir_all(to.parent().expect("parent")).map_err(io_at(&to, "creating"))?;
        std::fs::rename(&pj, &to).map_err(io_at(&to, "moving profile.json to"))?;
    }
    if install.path.join(crate::paths::game::CHAIN).is_file() {
        crate::chain::write_profile_section(&install.path, &[])?;
    }
    remove_if_there(&install.path.join(crate::paths::game::CHAIN_ONCE))?;
    remove_if_there(&install.path.join(crate::paths::game::PROFILE_ONCE))?;
    remove_if_there(&activation_record(dirs, install))?;
    Ok(())
}

pub fn active(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Activation>> {
    let Some(file) = read_game_profile(&install.path)? else { return Ok(None) };
    if let Ok(Some(a)) = crate::util::read_json::<Activation>(&activation_record(dirs, install))
        && a.profile_id == file.id
        && a.generation == file.generation
    {
        return Ok(Some(a));
    }
    Ok(Some(Activation {
        game_id: install.id.clone(),
        profile_id: file.id.clone(),
        generation: file.generation,
        path_mode: file.path_mode.unwrap_or(install.path_mode),
        profile_json: install.path.join(crate::paths::game::PROFILE).display().to_string(),
        copied: 0,
        fingerprint: file.fingerprint.clone().unwrap_or_default(),
        activated: file.written.clone().unwrap_or_default(),
    }))
}

/// kms1 over the profile's enabled entries (hash::fingerprint).
pub fn fingerprint(profile: &Profile) -> String {
    let entries = ModKind::ALL.iter().flat_map(|k| {
        let list = match k {
            ModKind::Kiln => &profile.mods.kiln,
            ModKind::Native => &profile.mods.native,
            ModKind::Plugin => &profile.mods.plugin,
            ModKind::Emf => &profile.mods.emf,
        };
        list.iter().filter(|m| m.enabled).map(move |m| crate::hash::FingerprintEntry {
            kind: k.as_str().into(),
            id: m.id.clone(),
            version: m.version.clone(),
            hash: m.hash.clone(),
        })
    });
    crate::hash::fingerprint(entries)
}
