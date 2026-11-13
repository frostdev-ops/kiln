//! Other engines' own loaders installed next to the game, and what that means for a profile's mods of that engine
//! (docs/kiln/engines/README.md "When the other loader is installed too").
//!
//! Every Kiln adapter steps aside when its engine's own loader runs in the process. That loader reads only its own
//! folder (`<game>/mods`, `emf/mods`, `greeneye` ...), never Kiln's Library, so the profile's mods of that engine run only
//! when Kiln copied them there ([`crate::engine_mods::place`]). This module answers, without starting anything:
//!
//! - [`find`]: which own loaders may run. A loader file under a proxy name next to the game, an active `[chain]` line
//!   of `kiln/chain.ini`, a `dinput8-chain.ini` entry, or (for EML) a profile that makes Kiln host ShroudForge, which
//!   then writes the EML packages of its mods folder that its switch has on (Kiln's copies carry that switch,
//!   engine_mods::place). A file counts as "may run": on Windows the game imports
//!   these names, under Wine/Proton they load only with a DLL override the launcher cannot see.
//! - [`notes`]: one line per engine for the launcher ("EML's own loader (dbghelp.dll) is installed: it runs the EML
//!   mods; Kiln copied this profile's 3 EML mods into mods/ for it").

use crate::engines::Engine;
use crate::manifest::ModKind;
use crate::pe::ProxyKind;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// How an own loader gets into the game.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Via {
    /// A file under a proxy name next to the game (`dbghelp.dll`, `winmm.dll` ...).
    Proxy,
    /// Kiln's proxy chains it (`kiln/chain.ini` `[chain]`).
    Chain,
    /// Kiln's proxy chains it from `dinput8-chain.ini` (EMF's list).
    EmfChain,
    /// Kiln hosts ShroudForge for this profile (it lists enabled ShroudForge mods); ShroudForge then owns startup
    /// patching and writes the EML packages of its mods folder whose `extended.mod.json` says `enabled: true`.
    HostedShroudForge,
}

/// How the profile's mods of a kind reach the own loader.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Delivery {
    /// The launcher copies them into the loader's folder at activation (engine_mods::place).
    Placed,
    /// The Kiln plugin copies them itself before the loader reads its folder (kiln-enshwick).
    Plugin,
    /// Nothing delivers them: the own loader cannot read Kiln's layout (shroudlink, Ember).
    None,
}

/// One own loader that may run.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnLoader {
    /// The mod kind whose mods it runs.
    pub kind: ModKind,
    /// The engine the file belongs to (ShroudForge's loader runs EML mods too).
    pub engine: Engine,
    /// Game-relative file (`dbghelp.dll`, `kiln/chain/eml-dinput8.dll`); for [`Via::HostedShroudForge`]
    /// `kiln/plugins/kiln-shroudforge.dll`.
    pub file: String,
    pub via: Via,
    /// Game-relative folder the loader reads (`mods`, `emf/mods`), '/' separated.
    pub folder: String,
    pub delivery: Delivery,
}

impl OwnLoader {
    /// The host folder the loader reads.
    pub fn root(&self, game_dir: &Path) -> PathBuf {
        if self.kind == ModKind::Eml && self.engine == Engine::ShroudForge {
            return crate::shroudforge::mods_dir(game_dir);
        }
        game_dir.join(&self.folder)
    }
}

/// The kinds a proxy classification runs, with the folder its loader reads and how Kiln delivers the profile's mods.
fn kinds_of(kind: ProxyKind, game_dir: &Path) -> Vec<(ModKind, String, Delivery)> {
    let sf_mods = || {
        let dir = crate::shroudforge::mods_dir(game_dir);
        dir.strip_prefix(game_dir).map(|p| p.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| dir.display().to_string())
    };
    match kind {
        ProxyKind::Eml => vec![(ModKind::Eml, "mods".into(), Delivery::Placed)],
        // ShroudForge's loader carries EML: it runs its own packages and the EML mods in its mods folder
        ProxyKind::ShroudForge => vec![(ModKind::ShroudForge, sf_mods(), Delivery::Placed), (ModKind::Eml, sf_mods(), Delivery::Placed)],
        ProxyKind::Emf => vec![(ModKind::Emf, "emf/mods".into(), Delivery::Placed)],
        ProxyKind::Set => vec![(ModKind::Set, crate::engine_mods::SET_MODS.into(), Delivery::Placed)],
        ProxyKind::Greeneye => vec![(ModKind::Greeneye, "greeneye".into(), Delivery::Placed)],
        ProxyKind::Flame => vec![(ModKind::Flame, "mods".into(), Delivery::Placed)],
        ProxyKind::Shroudtopia => vec![(ModKind::Shroudtopia, "mods".into(), Delivery::Placed)],
        ProxyKind::Enshwick => vec![(ModKind::Enshwick, "mods".into(), Delivery::Plugin)],
        ProxyKind::Shroudlink => vec![(ModKind::Shroudlink, crate::engine_mods::SHROUDLINK_MODS.into(), Delivery::None)],
        ProxyKind::Ember => vec![(ModKind::Ember, crate::engine_mods::EMBER_MODS.into(), Delivery::None)],
        _ => Vec::new(),
    }
}

/// `kiln/chain.ini` `[chain]` lines that are switched on, as (game-relative display path, host path).
fn chained(game_dir: &Path) -> Vec<(String, PathBuf)> {
    let Ok(ini) = crate::chain::read(game_dir) else { return Vec::new() };
    ini.chain.iter().filter(|e| e.status == crate::chain::EntryStatus::Ok && e.relative).map(|e| {
        let mut host = game_dir.join(crate::paths::game::KILN);
        for part in e.path.split('\\').filter(|s| !s.is_empty()) { host.push(part); }
        (format!("kiln/{}", e.path.replace('\\', "/")), host)
    }).collect()
}

/// `dinput8-chain.ini` `[Chain]` and `[EMF]` entries and the lines before any section, read as the proxy reads them
/// (relative to the game folder, EMF's rule; `chain::shared_chain_value`).
fn emf_chained(game_dir: &Path) -> Vec<(String, PathBuf)> {
    let p = game_dir.join(crate::paths::game::EMF_CHAIN);
    let Ok(bytes) = std::fs::read(&p) else { return Vec::new() };
    if bytes.len() > crate::chain::MAX_BYTES { return Vec::new(); }
    let text = crate::chain::decode(&bytes);
    let mut out = Vec::new();
    for line in crate::chain::lines(&text) {
        let crate::chain::IniLine::Text(section, text) = line else { continue };
        if section.is_some_and(|s| s != "chain" && s != "emf") { continue; }
        let value = crate::chain::shared_chain_value(text);
        if !value.to_ascii_lowercase().ends_with(".dll") { continue; }
        let (path, relative) = crate::chain::game_path(value);
        if !relative || path.split('\\').any(|s| s == "..") { continue; }
        let mut host = game_dir.to_path_buf();
        for part in path.split('\\').filter(|s| !s.is_empty()) { host.push(part); }
        out.push((path.replace('\\', "/"), host));
    }
    out
}

/// Every own loader that may run in this game folder, in a stable order (proxies next to the game, chain.ini,
/// dinput8-chain.ini, a hosted ShroudForge). One entry per (kind, file). `profile`: the profile being activated or
/// active (only its ShroudForge list matters here); None for none.
pub fn find(game_dir: &Path, profile: Option<&ProfileView>) -> Vec<OwnLoader> {
    let mut out: Vec<OwnLoader> = Vec::new();
    let mut add = |class: ProxyKind, file: String, via: Via| {
        let Some(engine) = Engine::of_proxy(class) else { return };
        for (kind, folder, delivery) in kinds_of(class, game_dir) {
            if !out.iter().any(|o| o.kind == kind && o.file.eq_ignore_ascii_case(&file)) {
                out.push(OwnLoader { kind, engine, file: file.clone(), via, folder, delivery });
            }
        }
    };
    // shroudlink's client addon (its dinput8.dll: the MODS tab) is not shroudlink's core. It starts a core of its own
    // only from `shroudlink_core.dll` in the game folder (singleplayer).
    let addon_only = |kind: ProxyKind, host: &Path| kind == ProxyKind::Shroudlink && crate::engines::shroudlink_addon_at(host)
        && !game_dir.join("shroudlink_core.dll").is_file();
    for slot in crate::engines::scan_proxies(game_dir) {
        if addon_only(slot.class.kind, &game_dir.join(&slot.name)) { continue; }
        add(slot.class.kind, slot.name, Via::Proxy);
    }
    for (file, host) in chained(game_dir) {
        if let Ok(c) = crate::pe::classify_proxy(&host) {
            if addon_only(c.kind, &host) { continue; }
            add(c.kind, file, Via::Chain);
        }
    }
    for (file, host) in emf_chained(game_dir) {
        if let Ok(c) = crate::pe::classify_proxy(&host) { add(c.kind, file, Via::EmfChain); }
    }
    // kiln-shroudforge hosts ShroudForge when the profile lists an enabled ShroudForge mod, or has ShroudForge drop-ins
    // on and ShroudForge's mods folder holds a ShroudForge package, and a ShroudForge runtime is there. ShroudForge then
    // owns the startup patch, runs the EML mods of its mods folder itself (those its switch has on: Kiln's copies there
    // carry it, engine_mods::place; an EML package is a folder with mod.json and no extended.mod.json, or one with launcher EML),
    // and kiln-eml stands aside for it through kiln.eml stand_aside (docs/kiln/eml.md section 6).
    // Listed after a proxy loader of EML's too: the adapter looks at no proxy file (its `wanted()`), and under Wine or
    // Proton a proxy without a DLL override is not loaded, so kiln-eml does not stand aside for it and ShroudForge runs
    // the EML mods ([`shroudforge_runs_eml`]). The proxy, listed first, still decides the folder they are placed in.
    let plugin = "kiln/plugins/kiln-shroudforge.dll";
    let runtime = game_dir.join("kiln/plugins/shroudforge/shroudforge-runtime.dll").is_file()
        || game_dir.join(crate::shroudforge::RUNTIME_DLL).is_file();
    // With drop-ins on, the adapter hosts ShroudForge when its `legacy_wanted` (native/plugins/shroudforge/src/plugin.cpp)
    // says so: a switched-on ShroudForge package in its mods folder, or a startup patch of an earlier start in its state
    // file (ShroudForge then puts the game data back). The launcher must reach the same answer: it places the profile's
    // EML mods for the loader that runs them (`sf_paths::adapter_hosts_dropins`).
    let hosts = profile.is_some_and(|p| p.enabled(ModKind::ShroudForge).next().is_some() || (p.dropins_shroudforge && crate::sf_paths::adapter_hosts_dropins(game_dir)));
    if hosts && runtime && game_dir.join(plugin).is_file() {
        let dir = crate::shroudforge::mods_dir(game_dir);
        let folder = dir.strip_prefix(game_dir).map(|p| p.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| dir.display().to_string());
        out.push(OwnLoader { kind: ModKind::Eml, engine: Engine::ShroudForge, file: plugin.into(), via: Via::HostedShroudForge, folder,
            delivery: Delivery::Placed });
    }
    out
}

/// The own loader of a kind that decides where its mods are placed (the first found), if any.
pub fn of_kind(loaders: &[OwnLoader], kind: ModKind) -> Option<&OwnLoader> {
    loaders.iter().find(|o| o.kind == kind)
}

/// ShroudForge may run the profile's EML mods this start: its own loader is installed, or Kiln hosts it for the profile
/// (also beside EML's own proxy file, see [`find`]). It then runs a package of its mods folder only while the package's
/// switch is on, so Kiln's copies there carry that switch (engine_mods::place); EML's own loader, when it runs instead,
/// reads only `launcher` from that file.
pub fn shroudforge_runs_eml(loaders: &[OwnLoader]) -> bool {
    loaders.iter().any(|o| o.kind == ModKind::Eml && o.engine == Engine::ShroudForge)
}

/// The kinds whose own loader reads a fixed folder of the game. EML, EMF and Shroudtopia mods are copied there only
/// while that loader is installed; Greeneye and Flame mods always run from Kiln's copy there
/// (engine_mods::run_folder), and SF and SET are always placed as well (their Kiln plugins read only their own folder).
pub const PLACED_FOR_LOADER: [ModKind; 5] = [ModKind::Eml, ModKind::Emf, ModKind::Greeneye, ModKind::Flame, ModKind::Shroudtopia];

/// The (id, enabled) lists of a profile, from a launcher profile or from the game's `kiln/profile.json`.
#[derive(Debug, Clone, Default)]
pub struct ProfileView {
    pub mods: Vec<(ModKind, String, bool)>,
    /// profile.json `dropins` is `true` or names `shroudforge` (kiln-shroudforge then hosts ShroudForge for the
    /// packages in its folder).
    pub dropins_shroudforge: bool,
    /// The Library folder of each mod (kind, id, host path) where the caller knows it: activation finds the player's own
    /// folder of a mod by that entry's manifest id ([`own_folder_of`]).
    pub sources: Vec<(ModKind, String, PathBuf)>,
}

fn names_shroudforge(d: &crate::profile::Dropins) -> bool {
    match d {
        crate::profile::Dropins::All(b) => *b,
        crate::profile::Dropins::Kinds(k) => k.contains(&ModKind::ShroudForge),
    }
}

impl ProfileView {
    pub fn of_profile(p: &crate::profile::Profile) -> Self {
        let mut mods = Vec::new();
        for k in ModKind::ALL {
            for m in p.mods.list(k) { mods.push((k, m.id.clone(), m.enabled)); }
        }
        ProfileView { mods, dropins_shroudforge: names_shroudforge(&p.dropins_file()), sources: Vec::new() }
    }

    pub fn of_file(f: &crate::profile::ProfileFile) -> Self {
        let mut mods = Vec::new();
        for k in ModKind::ALL {
            for m in f.mods.folders(k).into_iter().flatten() { mods.push((k, m.id.clone(), m.enabled)); }
        }
        ProfileView { mods, dropins_shroudforge: names_shroudforge(&f.dropins), sources: Vec::new() }
    }

    /// [`ProfileView::sources`] of a launcher profile's EML mods, from the Library.
    pub fn with_library(mut self, dirs: &crate::paths::DataDirs, p: &crate::profile::Profile) -> Self {
        for m in p.mods.list(ModKind::Eml) {
            let r = crate::library::LibraryRef { kind: ModKind::Eml, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
            if let Ok(path) = crate::library::path_of(dirs, &r) { self.sources.push((ModKind::Eml, m.id.clone(), path)); }
        }
        self
    }

    /// [`ProfileView::sources`] of the game's `kiln/profile.json` EML mods: their game-side paths on this system (a `Z:`
    /// path, a path relative to `<game>/kiln`, or a host path).
    pub fn with_file_sources(mut self, f: &crate::profile::ProfileFile, game_dir: &Path) -> Self {
        for m in f.mods.folders(ModKind::Eml).into_iter().flatten() {
            let p = m.path.trim();
            let host = crate::winpath::z_to_unix(p).or_else(|| {
                let h = PathBuf::from(p);
                if h.is_absolute() { Some(h) }
                else if crate::winpath::is_absolute_game_path(p) { None }
                else { Some(p.split(['\\', '/']).filter(|c| !c.is_empty()).fold(game_dir.join("kiln"), |d, c| if c == ".." { d.parent().map(Path::to_path_buf).unwrap_or(d) } else { d.join(c) })) }
            });
            if let Some(h) = host { self.sources.push((ModKind::Eml, m.id.clone(), h)); }
        }
        self
    }

    fn source(&self, kind: ModKind, id: &str) -> Option<&Path> {
        self.sources.iter().find(|(k, i, _)| *k == kind && i == id).map(|(_, _, p)| p.as_path())
    }

    pub fn enabled(&self, kind: ModKind) -> impl Iterator<Item = &str> {
        self.mods.iter().filter(move |(k, _, e)| *k == kind && *e).map(|(_, id, _)| id.as_str())
    }
}

/// What an own loader means for the profile's mods of its kind, for the launcher.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnLoaderNote {
    /// The loader (its `kind` is the note's).
    #[serde(flatten)]
    pub loader: OwnLoader,
    /// The profile's enabled mods of this kind.
    pub enabled: u32,
    /// Ids with Kiln's current copy in the loader's folder (they run under it).
    pub copied: Vec<String>,
    /// Ids that are not in the loader's folder: they do not load while the own loader runs.
    pub not_copied: Vec<String>,
    /// EML ids that a folder of the player's in the loader's folder stands for (found as activation finds it, which then
    /// places no copy and never writes into it), and that the loader takes as a mod. Whether it runs there is said by
    /// [`OwnLoaderNote::own_off`] and [`OwnLoaderNote::other_side`].
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own: Vec<String>,
    /// Ids of [`OwnLoaderNote::own`] that ShroudForge has switched off (their folder has no `extended.mod.json`, or its
    /// `enabled` is not true): ShroudForge does not run them, and activating the profile does not change that; the
    /// player switches them on in Kiln's menu (Mods, ShroudForge) or in ShroudForge's own Modloader.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_off: Vec<String>,
    /// EML ids that a folder of the player's stands for, which EML's own loader does not take as a mod (its mod.json is
    /// not one EML reads, or it has no src/mod.lua): the mod does not load, and activation places no copy beside it.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_refused: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`] that ShroudForge has switched off (EML mods while ShroudForge runs them: their
    /// copy has no `extended.mod.json`, or its `enabled` is not true): ShroudForge does not run them, and activating the
    /// profile again switches them on.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub off: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`] or [`OwnLoaderNote::own`] whose `extended.mod.json` targets the other side only (a server-only mod on a
    /// client, a client-only one on a dedicated server): ShroudForge does not run them here, and nothing needs doing.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub other_side: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`], or of a folder of the player's that stands for the mod, whose package ShroudForge cannot read (it refuses their `mod.json` or
    /// `extended.mod.json`): ShroudForge does not run them, and no switch changes that.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unreadable: Vec<String>,
    /// One sentence for the Play and profile pages.
    pub text: String,
}

/// True when `<root>` holds Kiln's copy of this kind's mod `id` (a `.kiln-library.json` marker says so): `<root>/<id>`,
/// or the folder an EML mod is placed in for its native DLL (`mods/XHL-Auto-Loot` for the Library id `xhl-auto-loot`).
pub fn has_copy(root: &Path, kind: ModKind, id: &str) -> bool {
    copy_of(root, kind, id).is_some()
}

/// The folder of [`has_copy`].
fn copy_of(root: &Path, kind: ModKind, id: &str) -> Option<PathBuf> {
    let is = |dir: &Path| crate::engine_mods::marker(dir).is_some_and(|m| m.kind == kind && m.id == id);
    let own = root.join(id);
    if is(&own) { return Some(own); }
    std::fs::read_dir(root).into_iter().flatten().flatten().map(|e| e.path()).find(|p| p.is_dir() && is(p))
}

/// The player's own folder in `root` that stands for the EML mod `id`, found as activation finds it
/// (engine_mods::place, which then places no copy): `<root>/<id>` when something is there that is not Kiln's copy, else a
/// folder in `root` without Kiln's marker of this kind whose mod.json id is the Library entry's (`src`) mod.json id, case
/// ignored (engine_mods::same_mod_elsewhere). Without `src` the Library id stands in for it. Other kinds: none.
fn own_folder_of(root: &Path, kind: ModKind, id: &str, src: Option<&Path>) -> Option<PathBuf> {
    if kind != ModKind::Eml { return None; }
    let dst = root.join(id);
    if std::fs::symlink_metadata(&dst).is_ok() && !crate::engine_mods::marker(&dst).is_some_and(|m| m.kind == kind && m.id == id) {
        return Some(dst);
    }
    match src {
        Some(src) => crate::engine_mods::same_mod_elsewhere(root, kind, src, &dst),
        None => {
            let mut dirs: Vec<PathBuf> = std::fs::read_dir(root).into_iter().flatten().flatten().map(|e| e.path()).collect();
            dirs.sort();
            dirs.into_iter().filter(|p| p.is_dir() && !crate::engine_mods::marker(p).is_some_and(|m| m.kind == kind))
                .find(|p| crate::engine_mods::manifest_id_raw(p).is_some_and(|other| other.eq_ignore_ascii_case(id)))
        }
    }
}

/// Does EML's own loader take the folder `dir` as a mod, and can it run it? Read from EML's own source (GPL, in
/// `gpl/kiln-eml/eml`; only its rules are restated here):
/// - its registry (`mod-loader-base/src/registry/mod.rs`, `ModRegistry::load` and `load_mod_at`) takes every folder of
///   its mods folder whose name does not start with `.` and whose `mod.json` serde_json reads as its `ModManifest`
///   (`registry/manifest.rs`): the strings `id` and `name`; `version` a semantic version (`semver::Version`: three
///   numbers that fit 64 bits, no leading zeros, optional pre-release and build parts); `capabilities` export, patch,
///   runtime or runtime-register-dll; each dependency an object with the string `id`, a `version` range and `optional`
///   absent, null or a boolean; `authors` strings; `description`, `license` and `icon` strings or null. No byte order
///   mark (serde_json refuses one). Nothing else is needed: a helper package (no patch or export capability, no
///   `src/mod.lua`) registers and serves other mods' `require`.
/// - its runner (`mod-loader-lua/src/runner/mod.rs`, `run`) requires `mods.<id>`, the mod's `src/mod.lua`, only for a
///   mod with the patch or the export capability: such a mod needs that file.
/// Err: why not. A dependency's version range is only checked to be a string here: EML's own range parser may refuse
/// one this accepts.
fn eml_takes(dir: &Path) -> Result<(), String> {
    if !dir.is_dir() { return Err("it is not a folder".into()); }
    if dir.file_name().is_some_and(|n| n.to_string_lossy().starts_with('.')) { return Err("its name starts with a dot, so EML skips it".into()); }
    let file = dir.join("mod.json");
    let b = match std::fs::metadata(&file) {
        Ok(m) if m.is_file() && m.len() <= 1024 * 1024 => std::fs::read(&file).map_err(|_| "its mod.json cannot be read".to_string())?,
        Ok(m) if m.is_file() => return Err("its mod.json is larger than 1 MiB".into()),
        _ => return Err("it has no mod.json".into()),
    };
    let v: serde_json::Value = serde_json::from_slice(&b).map_err(|_| "its mod.json is not valid JSON".to_string())?;
    let o = v.as_object().ok_or("its mod.json is not an object")?;
    let text = |k: &str| o.get(k).and_then(|v| v.as_str());
    if text("id").is_none() || text("name").is_none() { return Err("its mod.json lacks the id or the name".into()); }
    if !text("version").is_some_and(strict_semver) { return Err("its mod.json has no semantic version (such as 1.0.0)".into()); }
    let strings = |k: &str| o.get(k).is_none_or(|v| v.is_null() || v.is_string());
    if !strings("description") || !strings("license") || !strings("icon") { return Err("its mod.json has a description, license or icon that is not text".into()); }
    let list = |k: &str, ok: &dyn Fn(&serde_json::Value) -> bool| o.get(k).is_none_or(|v| v.as_array().is_some_and(|a| a.iter().all(ok)));
    if !list("capabilities", &|c| matches!(c.as_str(), Some("export" | "patch" | "runtime" | "runtime-register-dll"))) {
        return Err("its mod.json names a capability EML does not know".into());
    }
    if !list("authors", &|a| a.is_string()) { return Err("its mod.json has authors that are not text".into()); }
    if !list("dependencies", &|d| d.get("id").is_some_and(|i| i.is_string()) && d.get("version").is_some_and(|i| i.is_string())
        && d.get("optional").is_none_or(|x| x.is_null() || x.is_boolean())) {
        return Err("its mod.json has a dependency without an id, a version range or a true or false for optional".into());
    }
    let runs = o.get("capabilities").and_then(|c| c.as_array())
        .is_some_and(|a| a.iter().any(|c| matches!(c.as_str(), Some("patch" | "export"))));
    if runs && !dir.join("src").join("mod.lua").is_file() { return Err("it has the patch or export capability but no src/mod.lua".into()); }
    Ok(())
}

/// `MAJOR.MINOR.PATCH[-pre][+build]` as the semver crate parses it: the three numbers fit 64 bits, no leading zeros.
fn strict_semver(v: &str) -> bool {
    let (core, build) = match v.split_once('+') { Some((c, b)) => (c, Some(b)), None => (v, None) };
    let (nums, pre) = match core.split_once('-') { Some((n, p)) => (n, Some(p)), None => (core, None) };
    let digits = |n: &str| !n.is_empty() && n.bytes().all(|c| c.is_ascii_digit()) && (n == "0" || !n.starts_with('0'));
    let ident = |s: &str| !s.is_empty() && s.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-');
    let parts: Vec<&str> = nums.split('.').collect();
    parts.len() == 3 && parts.iter().all(|p| digits(p) && p.parse::<u64>().is_ok())
        && pre.is_none_or(|p| p.split('.').all(|i| ident(i) && (!i.bytes().all(|c| c.is_ascii_digit()) || digits(i))))
        && build.is_none_or(|b| b.split('.').all(ident))
}

fn names(ids: &[String]) -> String {
    match ids.len() {
        0 => String::new(),
        1..=3 => ids.join(", "),
        n => format!("{} and {} more", ids[..3].join(", "), n - 3),
    }
}

/// One note per kind that has an own loader here (the first loader found for the kind decides). Kinds whose
/// profile lists nothing still get a note, so the launcher can say the loader is there.
pub fn notes(game_dir: &Path, loaders: &[OwnLoader], profile: Option<&ProfileView>) -> Vec<OwnLoaderNote> {
    let mut out = Vec::new();
    for kind in ModKind::ALL {
        let Some(l) = of_kind(loaders, kind) else { continue };
        let root = l.root(game_dir);
        let ids: Vec<String> = profile.map(|p| p.enabled(kind).map(str::to_owned).collect()).unwrap_or_default();
        let (copied, not_copied): (Vec<String>, Vec<String>) = ids.iter().cloned().partition(|id| has_copy(&root, kind, id));
        // a folder of the player's stands for some of the rest (engine_mods::place copies none over or beside it)
        let theirs: Vec<(String, PathBuf)> = not_copied.iter()
            .filter_map(|id| own_folder_of(&root, kind, id, profile.and_then(|p| p.source(kind, id))).map(|d| (id.clone(), d))).collect();
        let not_copied: Vec<String> = not_copied.into_iter().filter(|id| !theirs.iter().any(|(t, _)| t == id)).collect();
        // ShroudForge runs an EML package of its folder only while the package's switch is on, for this side: when it may
        // run the EML mods (its own loader, or Kiln hosting it, beside EML's proxy file too) from the folder they are in
        let server = game_dir.join(crate::game::SERVER_EXE).is_file();
        let hosted = loaders.iter().find(|o| o.kind == kind && o.via == Via::HostedShroudForge);
        // EML's own loader, when it is the note's, runs instead of ShroudForge whenever it is loaded: always on Windows
        // and through a chain line, under Wine or Proton a proxy file only with a DLL override (the sentence below)
        let unloaded = cfg!(not(windows)) && l.via == Via::Proxy && !l.file.eq_ignore_ascii_case("dinput8.dll");
        let sf_reads = kind == ModKind::Eml && root == crate::shroudforge::mods_dir(game_dir)
            && (l.engine == Engine::ShroudForge || (hosted.is_some() && unloaded));
        let (mut off, mut other_side, mut unreadable, mut refused) = (Vec::new(), Vec::new(), Vec::new(), None);
        for id in copied.iter().filter(|_| sf_reads) {
            match copy_of(&root, kind, id).and_then(|dir| crate::shroudforge::switched_off(&dir, server)) {
                Some(crate::shroudforge::SfOff::Switch(_)) => off.push(id.clone()),
                Some(crate::shroudforge::SfOff::Side(_)) => other_side.push(id.clone()),
                Some(crate::shroudforge::SfOff::Unreadable(why)) => {
                    refused.get_or_insert(format!("{id}: {why}"));
                    unreadable.push(id.clone());
                }
                None => {}
            }
        }
        // a folder of the player's: does the loader that reads it take it, and does it run it here? Kiln never writes
        // into it, so a switch only the player can turn on
        let (mut own, mut own_off, mut own_refused, mut own_why) = (Vec::new(), Vec::new(), Vec::new(), None);
        for (id, dir) in &theirs {
            if sf_reads {
                match crate::shroudforge::switched_off(dir, server) {
                    None => own.push(id.clone()),
                    Some(crate::shroudforge::SfOff::Switch(_)) => { own.push(id.clone()); own_off.push(id.clone()); }
                    Some(crate::shroudforge::SfOff::Side(_)) => { own.push(id.clone()); other_side.push(id.clone()); }
                    Some(crate::shroudforge::SfOff::Unreadable(why)) => {
                        refused.get_or_insert(format!("{id}: {why}"));
                        unreadable.push(id.clone());
                    }
                }
            } else {
                match eml_takes(dir) {
                    Ok(()) => own.push(id.clone()),
                    Err(why) => { own_why.get_or_insert(format!("{id}: {why}")); own_refused.push(id.clone()); }
                }
            }
        }
        let who = loader_name(l);
        let label = short(kind);
        let folder = format!("{}/", l.folder.trim_end_matches('/'));
        let mut text = match (l.via, l.engine) {
            (Via::HostedShroudForge, _) => format!("Kiln hosts ShroudForge for this profile's ShroudForge mods: ShroudForge then runs the {label} mods in {folder}"),
            (_, Engine::ShroudForge) if kind == ModKind::Eml => format!("{who} ({}) is installed: it carries EML and runs the {label} mods in {folder}", l.file),
            _ => format!("{who} ({}) is installed: it runs the {label} mods in {folder}", l.file),
        };
        let n = ids.len();
        if n == 0 {
            text.push_str("; this profile has no enabled ");
            text.push_str(label);
            text.push_str(" mods");
        } else if not_copied.is_empty() {
            let placed = n - theirs.len();
            if placed > 0 {
                text.push_str(&match l.delivery {
                    Delivery::Plugin => format!("; Kiln's plugin copies this profile's {placed} {label} mod(s) there before it starts"),
                    _ => format!("; Kiln copied this profile's {placed} {label} mod(s) into {folder} for it"),
                });
            }
        } else {
            match l.delivery {
                Delivery::Plugin => text.push_str(&format!("; Kiln's plugin copies this profile's {n} {label} mod(s) there before it starts")),
                Delivery::None => text.push_str(&format!(
                    "; it cannot read Kiln's Library, so this profile's {n} {label} mod(s) do not load while it runs ({}). Remove {} to let Kiln run them",
                    names(&not_copied), l.file)),
                Delivery::Placed => text.push_str(&format!(
                    "; {} of this profile's {n} {label} mod(s) are not in {folder} and do not load while it runs ({}). Activate the profile again to copy them",
                    not_copied.len(), names(&not_copied))),
            }
        }
        if !own.is_empty() {
            let lead = if own.len() == n { format!("; this profile's {n} {label} mod(s)") } else { format!(". {} of this profile's {n} {label} mod(s)", own.len()) };
            text.push_str(&format!("{lead} are in your own folder(s) in {folder} ({}): Kiln places no copy of its own and never writes into a folder it did not place",
                names(&own)));
        }
        // On Windows the game imports these names, so the file always runs. A launcher built for Linux manages games
        // that run under Wine or Proton. Without the override the adapter hosts ShroudForge when the profile makes it
        // (find), and ShroudForge then runs the EML mods of its folder whose switch is on; else kiln-eml runs them.
        if unloaded {
            text.push_str(". Under Wine or Proton it runs only with a DLL override; ");
            text.push_str(&match hosted {
                Some(_) if sf_reads => "without one, ShroudForge (hosted by Kiln for this profile's ShroudForge mods) runs them, each while its \
                    ShroudForge switch is on (Kiln switches it on in its copies)".to_string(),
                Some(h) => format!("without one, ShroudForge (hosted by Kiln for this profile's ShroudForge mods) runs only the {label} mods in \
                    {}/, and Kiln's copies are not there", h.folder.trim_end_matches('/')),
                None => "without one Kiln runs these mods itself".to_string(),
            });
        }
        if !off.is_empty() {
            text.push_str(&format!(
                ". ShroudForge has {} of them switched off in ShroudForge's settings and does not run them ({}): activate the profile again to switch them on",
                off.len(), names(&off)));
        }
        if !own_off.is_empty() {
            text.push_str(&format!(
                ". ShroudForge has your own folder(s) of {} of them switched off and does not run them ({}): activating the profile does not change \
                that, because Kiln never writes into your own folder. Switch them on in Kiln's in-game menu (Mods, ShroudForge) or in ShroudForge's \
                own Modloader, then start the game again", own_off.len(), names(&own_off)));
        }
        if !own_refused.is_empty() {
            text.push_str(&format!(
                ". {who} does not take your own folder(s) of {} of them as a mod ({}), so they do not load, and Kiln places no copy beside a \
                folder of yours that holds the same mod: fix or remove that folder, then activate the profile again",
                own_refused.len(), own_why.unwrap_or_default()));
        }
        if !other_side.is_empty() {
            text.push_str(&format!(". {} of them target the {} only, so ShroudForge does not run them {} ({}); nothing needs doing",
                other_side.len(), if server { "client" } else { "server" }, if server { "on this server" } else { "in this game" }, names(&other_side)));
        }
        if !unreadable.is_empty() {
            text.push_str(&format!(". ShroudForge cannot read {} of them, so it does not run them and no switch changes that ({}{}): a version \
                of the mod whose files ShroudForge reads does, or a profile without ShroudForge mods (Kiln then runs them itself)",
                unreadable.len(), if unreadable.len() == 1 { String::new() } else { format!("{}; ", names(&unreadable)) }, refused.unwrap_or_default()));
        }
        out.push(OwnLoaderNote { loader: l.clone(), enabled: n as u32, copied, not_copied, own, own_off, own_refused, off, other_side, unreadable, text });
    }
    out
}

/// "EML's own loader", "ShroudForge's loader (it runs EML mods too)".
pub fn loader_name(l: &OwnLoader) -> String {
    match (l.kind, l.engine, l.via) {
        (ModKind::Eml, Engine::ShroudForge, Via::HostedShroudForge) => "ShroudForge, hosted by Kiln for this profile,".into(),
        (_, Engine::ShroudForge, _) => "ShroudForge's loader".into(),
        (k, _, _) => format!("{}'s own loader", short(k)),
    }
}

/// The short engine name of a kind, for sentences ("EML", "Greeneye").
pub fn short(kind: ModKind) -> &'static str {
    match kind {
        ModKind::Eml => "EML",
        ModKind::Emf => "EMF",
        ModKind::ShroudForge => "ShroudForge",
        ModKind::Set => "SET",
        ModKind::Greeneye => "Greeneye",
        ModKind::Flame => "Flame",
        ModKind::Shroudtopia => "Shroudtopia",
        ModKind::Enshwick => "Enshwick",
        ModKind::Shroudlink => "shroudlink",
        ModKind::Ember => "Ember",
        k => k.label(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("kiln-ownloader-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("kiln")).unwrap();
        d
    }

    fn dll(text: &[u8]) -> Vec<u8> {
        crate::pe::build_test_dll(&[], text)
    }

    #[test]
    fn finds_proxies_chain_lines_and_emf_lists() {
        let g = tmp("find");
        assert!(find(&g, None).is_empty());
        std::fs::write(g.join("dbghelp.dll"), dll(b"EML_LOG_FILE_PATH")).unwrap();
        let l = find(&g, None);
        assert_eq!(l.len(), 1);
        assert_eq!((l[0].kind, l[0].engine, l[0].via, l[0].folder.as_str()), (ModKind::Eml, Engine::Eml, Via::Proxy, "mods"));
        std::fs::create_dir_all(g.join("kiln/chain")).unwrap();
        std::fs::write(g.join("kiln/chain/flame-dinput8.dll"), dll(b"FlameStartRuntime flame.json")).unwrap();
        std::fs::write(g.join("kiln/chain/greeneye-dinput8.dll"), dll(b"greeneye")).unwrap();
        std::fs::write(g.join("kiln/chain.ini"), "[chain]\nchain\\flame-dinput8.dll\n;chain\\greeneye-dinput8.dll\n").unwrap();
        let l = find(&g, None);
        let flame = of_kind(&l, ModKind::Flame).unwrap();
        assert_eq!((flame.via, flame.file.as_str()), (Via::Chain, "kiln/chain/flame-dinput8.dll"));
        assert!(of_kind(&l, ModKind::Greeneye).is_none(), "a switched-off chain line does not run");
        std::fs::create_dir_all(g.join("emf")).unwrap();
        std::fs::write(g.join("emf/emf-loader.dll"), crate::pe::build_test_dll(&["emf_loader_identity"], b"")).unwrap();
        std::fs::write(g.join("dinput8-chain.ini"), "[Chain]\nemf\\emf-loader.dll\n..\\x.dll\n").unwrap();
        let emf = of_kind(&find(&g, None), ModKind::Emf).cloned().unwrap();
        assert_eq!((emf.via, emf.folder.as_str()), (Via::EmfChain, "emf/mods"));
        // the shared spec's forms, as the proxy reads them: a quoted name, a comment after it, a line before any section
        for ini in ["[Chain]\n\"emf\\emf-loader.dll\"\n", "[EMF]\nemf\\emf-loader.dll ; EMF\n", "emf/emf-loader.dll\t# EMF\n"] {
            std::fs::write(g.join("dinput8-chain.ini"), ini).unwrap();
            let emf = of_kind(&find(&g, None), ModKind::Emf).cloned();
            assert_eq!(emf.map(|e| e.via), Some(Via::EmfChain), "{ini:?}");
        }
        std::fs::write(g.join("dinput8-chain.ini"), "[Other]\nemf\\emf-loader.dll\n").unwrap();
        assert!(of_kind(&find(&g, None), ModKind::Emf).is_none(), "another tool's section is not read");
        std::fs::remove_dir_all(g).unwrap();
    }

    #[test]
    fn shroudforge_carries_eml_and_hosting_counts_with_a_profile() {
        let g = tmp("sf");
        std::fs::write(g.join("dbghelp.dll"), dll(b"EML_LOG_FILE_PATH shroudforge")).unwrap();
        let l = find(&g, None);
        let eml = of_kind(&l, ModKind::Eml).unwrap();
        assert_eq!((eml.engine, eml.folder.as_str()), (Engine::ShroudForge, "mods"));
        std::fs::remove_file(g.join("dbghelp.dll")).unwrap();
        std::fs::create_dir_all(g.join("kiln/plugins")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        let mut view = ProfileView::default();
        view.mods.push((ModKind::ShroudForge, "other".into(), true));
        assert!(find(&g, Some(&view)).is_empty(), "no ShroudForge runtime: nothing is hosted");
        view.mods.clear();
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        assert!(find(&g, Some(&view)).is_empty());
        view.mods.push((ModKind::ShroudForge, "sfmod".into(), false));
        assert!(find(&g, Some(&view)).is_empty(), "a switched-off ShroudForge mod does not make Kiln host it");
        view.mods.push((ModKind::ShroudForge, "other".into(), true));
        let l = find(&g, Some(&view));
        assert_eq!(l[0].via, Via::HostedShroudForge);
        assert_eq!(l[0].kind, ModKind::Eml);
        // drop-ins on: hosted for a ShroudForge package in its folder, not for an EML-only folder
        let mut view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        std::fs::create_dir_all(g.join("mods/eml-only")).unwrap();
        std::fs::write(g.join("mods/eml-only/mod.json"), br#"{"id":"e","name":"e","version":"1.0.0"}"#).unwrap();
        assert!(find(&g, Some(&view)).is_empty());
        std::fs::create_dir_all(g.join("mods/sfpack")).unwrap();
        std::fs::write(g.join("mods/sfpack/mod.json"), br#"{"id":"s","name":"s","version":"1.0.0"}"#).unwrap();
        std::fs::write(g.join("mods/sfpack/extended.mod.json"), br#"{"launcher":"SF"}"#).unwrap();
        assert_eq!(find(&g, Some(&view))[0].via, Via::HostedShroudForge);
        view.dropins_shroudforge = false;
        assert!(find(&g, Some(&view)).is_empty());
        std::fs::remove_dir_all(g).unwrap();
    }

    /// kiln-shroudforge's `legacy_wanted` (plugin.cpp) hosts ShroudForge with drop-ins on when its state file records a
    /// startup patch of an earlier start, also without a ShroudForge package in its folder. The launcher must then give
    /// Kiln's EML copies ShroudForge's switch, or ShroudForge, which runs them that start, keeps them off.
    #[test]
    fn dropins_host_shroudforge_after_an_earlier_startup_patch_as_the_adapter_does() {
        let g = tmp("sf-patched");
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        std::fs::create_dir_all(g.join("mods/eml-only")).unwrap();
        std::fs::write(g.join("mods/eml-only/mod.json"), br#"{"id":"e","name":"e","version":"1.0.0"}"#).unwrap();
        let view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        assert!(find(&g, Some(&view)).is_empty(), "no ShroudForge package and no earlier patch");
        std::fs::create_dir_all(g.join("shroudforge")).unwrap();
        for (status, hosted) in [("applied", true), ("preparing", true), ("failed", true), ("restored", false)] {
            std::fs::write(g.join("shroudforge/state.json"), format!(r#"{{"assets":{{"status":"{status}"}}}}"#)).unwrap();
            assert_eq!(find(&g, Some(&view)).first().map(|l| l.via) == Some(Via::HostedShroudForge), hosted, "{status}");
        }
        // ShroudForge's own setting for its state folder is followed
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::create_dir_all(g.join("sfstate")).unwrap();
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"state":"sfstate"}}"#).unwrap();
        std::fs::write(g.join("shroudforge/state.json"), br#"{"assets":{"status":"applied"}}"#).unwrap();
        assert!(find(&g, Some(&view)).is_empty(), "the configured state folder has no record");
        std::fs::write(g.join("sfstate/state.json"), br#"{"assets":{"status":"applied"}}"#).unwrap();
        assert_eq!(find(&g, Some(&view))[0].via, Via::HostedShroudForge);
        assert!(find(&g, Some(&ProfileView::default())).is_empty(), "drop-ins off: the record alone does not host it");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Player report (Nexus, 13 Nov 2026): "Auto Loot Plus is turned off by ShroudForge". A profile EML mod that the
    /// player's own folder in ShroudForge's mods folder serves (a hand install, no Kiln marker) is not "not copied":
    /// activation never copies over it. ShroudForge keeps it off while it has no switch, and only the player can switch
    /// it on, because Kiln never writes into a folder it did not place.
    #[test]
    fn a_players_own_eml_folder_is_named_and_one_shroudforge_has_off_says_where_to_switch_it_on() {
        let g = tmp("own-eml");
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        let mut view = ProfileView::default();
        view.mods.push((ModKind::ShroudForge, "sf-unlimited-flight".into(), true));
        for id in ["kilns", "auto-loot-plus", "missing"] { view.mods.push((ModKind::Eml, id.into(), true)); }
        let kilns = g.join("mods/kilns");
        std::fs::create_dir_all(&kilns).unwrap();
        let m = crate::engine_mods::Marker { format: 1, kind: ModKind::Eml, id: "kilns".into(), version: "1.0.0".into(), hash: "h".into() };
        crate::util::write_json_atomic(&kilns.join(crate::engine_mods::MARKER), &m).unwrap();
        std::fs::write(kilns.join("mod.json"), br#"{"id":"kilns","name":"k","version":"1.0.0"}"#).unwrap();
        std::fs::write(kilns.join("extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"launcher":"EML"}"#).unwrap();
        // the hand install, under another folder name, as a legacy EML package (no extended.mod.json)
        let own = g.join("mods/AutoLootPlus");
        std::fs::create_dir_all(own.join("src")).unwrap();
        std::fs::write(own.join("mod.json"), br#"{"id":"auto-loot-plus","name":"Auto Loot Plus","version":"1.1.8","capabilities":["patch"]}"#).unwrap();
        std::fs::write(own.join("src/mod.lua"), b"return {}").unwrap();
        let l = find(&g, Some(&view));
        let n = notes(&g, &l, Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert_eq!((e.copied.clone(), e.own.clone(), e.not_copied.clone()),
            (vec!["kilns".to_string()], vec!["auto-loot-plus".to_string()], vec!["missing".to_string()]));
        assert_eq!(e.own_off, vec!["auto-loot-plus".to_string()]);
        assert!(e.off.is_empty(), "{e:?}");
        assert!(e.text.contains("1 of this profile's 3 EML mod(s) are not in mods/ and do not load while it runs (missing)"), "{}", e.text);
        assert!(e.text.contains("1 of this profile's 3 EML mod(s) are in your own folder(s) in mods/ (auto-loot-plus)"), "{}", e.text);
        assert!(e.text.contains("Switch them on in Kiln's in-game menu (Mods, ShroudForge) or in ShroudForge's own Modloader"), "{}", e.text);
        // switched on there (ShroudForge's Modloader writes the file): it runs, and nothing asks for a switch
        std::fs::write(own.join("extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{}}"#).unwrap();
        let n = notes(&g, &l, Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert!(e.own_off.is_empty() && e.own == vec!["auto-loot-plus".to_string()], "{e:?}");
        assert!(!e.text.contains("switched off"), "{}", e.text);
        std::fs::remove_dir_all(g).unwrap();
    }

    /// A game folder with Kiln's ShroudForge plugin and runtime, and a profile with ShroudForge drop-ins on.
    fn sf_game(name: &str) -> (PathBuf, ProfileView) {
        let g = tmp(name);
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        (g, ProfileView { dropins_shroudforge: true, ..ProfileView::default() })
    }

    fn hosted(g: &Path, view: &ProfileView) -> bool {
        find(g, Some(view)).iter().any(|l| l.via == Via::HostedShroudForge)
    }

    const APPLIED: &[u8] = br#"{"assets":{"status":"applied"}}"#;

    /// Review finding 1: the adapter reads any state file ifstream opens (plugin.cpp `read_text`): a large one, one
    /// behind a link, one under a path with `..`. The launcher refused those and placed the EML mods for kiln-eml while
    /// ShroudForge ran them.
    #[test]
    fn dropins_read_large_linked_and_dotted_state_files_as_the_adapter_does() {
        let (g, view) = sf_game("sf-read");
        // a 3 MiB state file (over the 2 MiB of the launcher's report reader)
        let mut big = br#"{"assets":{"status":"applied"},"pad":""#.to_vec();
        big.extend(std::iter::repeat_n(b'x', 3 * 1024 * 1024));
        big.extend(br#""}"#);
        std::fs::write(g.join("shroudforge/state.json"), &big).unwrap();
        assert!(hosted(&g, &view), "a 3 MiB state file");
        // a link to the state file
        std::fs::write(g.join("real-state.json"), APPLIED).unwrap();
        std::fs::remove_file(g.join("shroudforge/state.json")).unwrap();
        std::os::unix::fs::symlink(g.join("real-state.json"), g.join("shroudforge/state.json")).unwrap();
        assert!(hosted(&g, &view), "a linked state file");
        // `..` in paths.state
        std::fs::remove_file(g.join("shroudforge/state.json")).unwrap();
        std::fs::create_dir_all(g.join("sub")).unwrap();
        std::fs::create_dir_all(g.join("sfstate")).unwrap();
        std::fs::write(g.join("sfstate/state.json"), APPLIED).unwrap();
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"state":"sub\\..\\sfstate"}}"#).unwrap();
        assert!(hosted(&g, &view), "paths.state with ..");
        // a value of another type: the adapter falls back to its default folder
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"state":7}}"#).unwrap();
        assert!(!hosted(&g, &view), "default folder, no record there");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Review finding 2: `paths.state` is a Windows path for the game. Under Proton `C:\sf-state` is in the prefix's
    /// drive_c, case is ignored, and Windows drops trailing spaces of a name but keeps leading ones.
    #[test]
    fn dropins_find_the_state_folder_with_windows_path_rules() {
        let t = tmp("sf-winpath");
        let g = t.join("lib/steamapps/common/Enshrouded");
        std::fs::create_dir_all(&g).unwrap();
        std::fs::rename(t.join("kiln"), g.join("kiln")).unwrap();
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        let view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        let pfx = t.join("lib/steamapps/compatdata/1203620/pfx");
        std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
        std::fs::create_dir_all(pfx.join("drive_c/sf-state")).unwrap();
        std::os::unix::fs::symlink("../drive_c", pfx.join("dosdevices/c:")).unwrap();
        std::fs::write(pfx.join("drive_c/sf-state/state.json"), APPLIED).unwrap();
        let config = |v: &str| std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), serde_json::json!({"paths": {"state": v}}).to_string()).unwrap();
        config(r"C:\sf-state");
        assert!(hosted(&g, &view), "C: of the Proton prefix");
        config(r"\\?\C:\SF-STATE");
        assert!(hosted(&g, &view), "verbatim, another case");
        std::fs::create_dir_all(g.join("sfstate")).unwrap();
        std::fs::write(g.join("sfstate/state.json"), APPLIED).unwrap();
        config("SFState");
        assert!(hosted(&g, &view), "a relative folder in another case");
        config("sfstate. ");
        assert!(hosted(&g, &view), "trailing dot and space dropped");
        config(" sfstate");
        assert!(!hosted(&g, &view), "a leading space is part of the name");
        config(r"D:\nowhere");
        assert!(hosted(&g, &view), "a drive the prefix does not map: open, so hosted (see the module's note)");
        std::fs::remove_dir_all(t).unwrap();
    }

    /// Review finding 3: the adapter reads a linked config too. The launcher ignored it and judged by the default state
    /// folder's old record. And a ShroudForge package switched off does not count for the adapter.
    #[test]
    fn dropins_read_a_linked_config_and_count_only_packages_the_adapter_counts() {
        let (g, view) = sf_game("sf-config");
        std::fs::write(g.join("shroudforge/state.json"), APPLIED).unwrap();  // an old record in the default folder
        std::fs::create_dir_all(g.join("now")).unwrap();
        std::fs::write(g.join("real-config.json"), br#"{"paths":{"state":"now"}}"#).unwrap();
        std::os::unix::fs::symlink(g.join("real-config.json"), g.join(crate::shroudforge::CONFIG_FILE)).unwrap();
        assert!(!hosted(&g, &view), "the configured folder has no record");
        std::fs::create_dir_all(g.join("mods/sfpack")).unwrap();
        std::fs::write(g.join("mods/sfpack/mod.json"), br#"{"id":"s","name":"s","version":"1.0.0"}"#).unwrap();
        std::fs::write(g.join("mods/sfpack/extended.mod.json"), br#"{"launcher":"SF","enabled":false}"#).unwrap();
        assert!(!hosted(&g, &view), "a ShroudForge package switched off");
        std::fs::write(g.join("mods/sfpack/extended.mod.json"), br#"{"launcher":"SF","enabled":"yes"}"#).unwrap();
        assert!(hosted(&g, &view), "an enabled of another type: nlohmann throws, the adapter's catch counts it");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// A ShroudForge-hosted profile with the EML mods `ids` on, and the Library folder of `id` with `manifest_id`.
    fn sf_hosted_with(name: &str, ids: &[&str]) -> (PathBuf, ProfileView) {
        let (g, mut view) = sf_game(name);
        view.dropins_shroudforge = false;
        view.mods.push((ModKind::ShroudForge, "sf-unlimited-flight".into(), true));
        for id in ids { view.mods.push((ModKind::Eml, (*id).into(), true)); }
        (g, view)
    }

    fn eml_folder(dir: &Path, mod_json: &str, ext: Option<&str>) {
        std::fs::create_dir_all(dir.join("src")).unwrap();
        std::fs::write(dir.join("mod.json"), mod_json).unwrap();
        std::fs::write(dir.join("src/mod.lua"), b"return {}").unwrap();
        if let Some(e) = ext { std::fs::write(dir.join("extended.mod.json"), e).unwrap(); }
    }

    /// Review finding 4: activation finds the player's folder by the Library entry's manifest id, which may differ from
    /// the Library id (an id over 64 letters is cut to 64 there).
    #[test]
    fn a_players_folder_is_found_by_the_library_entrys_manifest_id() {
        let long = "a".repeat(65);
        let cut = "a".repeat(64);
        let (g, mut view) = sf_hosted_with("own-long", &[&cut]);
        let lib = g.join("library/eml").join(&cut);
        eml_folder(&lib, &format!(r#"{{"id":"{long}","name":"L","version":"1.0.0"}}"#), None);
        view.sources.push((ModKind::Eml, cut.clone(), lib));
        eml_folder(&g.join("mods/LongMod"), &format!(r#"{{"id":"{long}","name":"L","version":"1.0.0"}}"#), None);
        let n = notes(&g, &find(&g, Some(&view)), Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert_eq!((e.own.clone(), e.own_off.clone(), e.not_copied.clone()), (vec![cut.clone()], vec![cut.clone()], Vec::<String>::new()), "{e:?}");
        assert!(!e.text.contains("Activate the profile again to copy"), "{}", e.text);
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Review finding 5: a player's folder ShroudForge runs on the other side only, or one whose extended.mod.json it
    /// refuses, keeps that reason.
    #[test]
    fn a_players_folder_for_the_other_side_or_refused_says_so() {
        let (g, view) = sf_hosted_with("own-side", &["srv", "bad"]);
        eml_folder(&g.join("mods/srv"), r#"{"id":"srv","name":"S","version":"1.0.0"}"#, Some(r#"{"schemaVersion":1,"enabled":true,"targets":["server"],"launcher":"EML"}"#));
        eml_folder(&g.join("mods/bad"), r#"{"id":"bad","name":"B","version":"1.0.0"}"#, Some(r#"{"schemaVersion":1,"enabled":"yes","launcher":"EML"}"#));
        let n = notes(&g, &find(&g, Some(&view)), Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert_eq!((e.other_side.clone(), e.unreadable.clone()), (vec!["srv".to_string()], vec!["bad".to_string()]), "{e:?}");
        assert_eq!(e.own, vec!["srv".to_string()], "the refused folder is not one the loader takes");
        assert!(e.own_off.is_empty() && e.not_copied.is_empty(), "{e:?}");
        assert!(e.text.contains("target the server only") && e.text.contains("ShroudForge cannot read 1 of them"), "{}", e.text);
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Review finding 6: a folder serves a mod only when the loader that reads it takes it. EML's own loader refuses a
    /// mod.json without a name and a version; the profile has no ShroudForge mods here.
    #[test]
    fn a_players_folder_eml_refuses_is_said_and_not_counted_as_serving() {
        let g = tmp("own-invalid");
        std::fs::write(g.join("dbghelp.dll"), dll(b"EML_LOG_FILE_PATH")).unwrap();
        let mut view = ProfileView::default();
        view.mods.push((ModKind::Eml, "example".into(), true));
        view.mods.push((ModKind::Eml, "good".into(), true));
        eml_folder(&g.join("mods/example"), r#"{"id":"example"}"#, None);
        eml_folder(&g.join("mods/good"), r#"{"id":"good","name":"G","version":"1.2.3","capabilities":["patch"]}"#, None);
        let n = notes(&g, &find(&g, None), Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert_eq!((e.own.clone(), e.own_refused.clone()), (vec!["good".to_string()], vec!["example".to_string()]), "{e:?}");
        assert!(e.text.contains("does not take your own folder(s) of 1 of them as a mod (example: its mod.json lacks the id or the name)"), "{}", e.text);
        // a version EML's reader refuses
        eml_folder(&g.join("mods/example"), r#"{"id":"example","name":"E","version":"1.0"}"#, None);
        let n = notes(&g, &find(&g, None), Some(&view));
        assert_eq!(n[0].own_refused, vec!["example".to_string()]);
        eml_folder(&g.join("mods/example"), r#"{"id":"example","name":"E","version":"1.0.0-beta.1+b"}"#, None);
        let n = notes(&g, &find(&g, None), Some(&view));
        assert!(n[0].own_refused.is_empty() && n[0].own.len() == 2, "{:?}", n[0]);
        std::fs::remove_dir_all(g).unwrap();
    }

    /// A Steam library layout: `<t>/lib/steamapps/common/<folder>` with `exes`, Kiln's ShroudForge plugin and runtime,
    /// and the Proton prefix of `app` with `c:` linked to its drive_c.
    fn proton_game(name: &str, folder: &str, exes: &[&str], apps: &[u32]) -> (PathBuf, PathBuf) {
        let t = tmp(name);
        let g = t.join("lib/steamapps/common").join(folder);
        std::fs::create_dir_all(g.join("kiln/plugins/shroudforge")).unwrap();
        std::fs::write(g.join("kiln/plugins/kiln-shroudforge.dll"), b"x").unwrap();
        std::fs::write(g.join("kiln/plugins/shroudforge/shroudforge-runtime.dll"), b"x").unwrap();
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        for exe in exes { std::fs::write(g.join(exe), b"MZ").unwrap(); }
        for app in apps {
            let pfx = t.join(format!("lib/steamapps/compatdata/{app}/pfx"));
            std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
            std::fs::create_dir_all(pfx.join("drive_c")).unwrap();
            std::os::unix::fs::symlink("../drive_c", pfx.join("dosdevices/c:")).unwrap();
        }
        (t, g)
    }

    /// Rerun 2, finding 1: the folder Kiln copies the profile's EML mods into is the one the hosting decision looked at.
    /// `paths.mods` = `C:\sfmods` under Proton is in the prefix's drive_c; the old resolver fell back to `<game>/mods`.
    #[test]
    fn placement_and_hosting_resolve_shroudforge_s_mods_folder_alike() {
        let (t, g) = proton_game("r2-mods", "Enshrouded", &[crate::game::CLIENT_EXE], &[crate::CLIENT_APP_ID]);
        let sfmods = t.join("lib/steamapps/compatdata/1203620/pfx/drive_c/sfmods");
        std::fs::create_dir_all(sfmods.join("pack")).unwrap();
        std::fs::write(sfmods.join("pack/extended.mod.json"), br#"{"launcher":"SF"}"#).unwrap();
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"mods":"C:\\sfmods"}}"#).unwrap();
        assert_eq!(crate::shroudforge::mods_dir(&g), sfmods);
        let view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        let l = find(&g, Some(&view));
        let host = l.iter().find(|l| l.via == Via::HostedShroudForge).expect("hosted for the package in C:\\sfmods");
        assert_eq!(host.root(&g), sfmods);
        std::fs::remove_dir_all(t).unwrap();
    }

    /// Rerun 2, finding 2: a dedicated server's folder takes the server app's prefix, not the client's. With both
    /// executables (or neither) no Steam prefix is taken: drive paths then stay unnamed and the answer is "hosted".
    #[test]
    fn a_server_folder_takes_the_server_app_s_prefix() {
        let apps = [crate::CLIENT_APP_ID, crate::SERVER_APP_ID];
        let (t, g) = proton_game("r2-server", "EnshroudedServer", &[crate::game::SERVER_EXE], &apps);
        let server_c = t.join("lib/steamapps/compatdata/2278520/pfx/drive_c");
        std::fs::create_dir_all(server_c.join("sf-state")).unwrap();
        std::fs::create_dir_all(t.join("lib/steamapps/compatdata/1203620/pfx/drive_c/sf-state")).unwrap();
        std::fs::write(server_c.join("sf-state/state.json"), APPLIED).unwrap();
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"state":"C:\\sf-state"}}"#).unwrap();
        let view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        assert!(hosted(&g, &view), "the server prefix's C: holds the record");
        // the client's prefix with the record, the server's without: not hosted
        std::fs::remove_file(server_c.join("sf-state/state.json")).unwrap();
        std::fs::write(t.join("lib/steamapps/compatdata/1203620/pfx/drive_c/sf-state/state.json"), APPLIED).unwrap();
        assert!(!hosted(&g, &view), "the client's prefix is not this folder's");
        assert_eq!(crate::sf_paths::wine_prefix(&g), Some(t.join("lib/steamapps/compatdata/2278520/pfx")));
        std::fs::write(g.join(crate::game::CLIENT_EXE), b"MZ").unwrap();
        assert_eq!(crate::sf_paths::wine_prefix(&g), None, "both executables: no Steam prefix");
        std::fs::remove_dir_all(t).unwrap();
    }

    /// Rerun 2, finding 3: Windows finds the config, `state.json` and `extended.mod.json` whatever their case.
    #[test]
    fn shroudforge_s_files_are_found_without_regard_to_case() {
        let (g, view) = sf_game("r2-case");
        std::fs::write(g.join("shroudforge/STATE.JSON"), APPLIED).unwrap();
        assert!(hosted(&g, &view), "STATE.JSON in the default folder");
        std::fs::remove_file(g.join("shroudforge/STATE.JSON")).unwrap();
        std::fs::remove_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::create_dir_all(g.join("shroudforge/Config")).unwrap();
        std::fs::create_dir_all(g.join("now")).unwrap();
        std::fs::write(g.join("shroudforge/state.json"), APPLIED).unwrap();  // old record in the default folder
        std::fs::write(g.join("shroudforge/Config/ModLoader-Config.json"), br#"{"paths":{"state":"now"}}"#).unwrap();
        assert!(!hosted(&g, &view), "the config in other case moves the state folder");
        std::fs::create_dir_all(g.join("mods/pack")).unwrap();
        std::fs::write(g.join("mods/pack/Extended.Mod.Json"), br#"{"launcher":"SF"}"#).unwrap();
        assert!(hosted(&g, &view), "a package whose file is in other case");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Rerun 2, finding 7: a state file of any size gets the adapter's answer (the old 64 MiB limit answered "hosted"),
    /// and only the wanted members are kept while it is parsed.
    #[test]
    fn a_state_file_over_the_old_limit_gets_the_adapter_s_answer() {
        let (g, view) = sf_game("r2-big");
        let mut f = std::io::BufWriter::new(std::fs::File::create(g.join("shroudforge/state.json")).unwrap());
        use std::io::Write;
        f.write_all(br#"{"pad":["#).unwrap();
        for i in 0..(33 * 1024 * 1024) { f.write_all(if i == 0 { b"0" } else { b",0" }).unwrap(); }
        f.write_all(br#"],"assets":{"status":"restored"}}"#).unwrap();
        drop(f);
        assert!(std::fs::metadata(g.join("shroudforge/state.json")).unwrap().len() > 64 * 1024 * 1024);
        assert!(!hosted(&g, &view), "no startup record in it");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Rerun 2, finding 9: `enabled: false` ends the adapter's check of a package before `launcher` is read.
    #[test]
    fn a_package_switched_off_is_off_whatever_its_launcher() {
        let (g, view) = sf_game("r2-short");
        std::fs::create_dir_all(g.join("mods/pack")).unwrap();
        std::fs::write(g.join("mods/pack/extended.mod.json"), br#"{"enabled":false,"launcher":7}"#).unwrap();
        assert!(!hosted(&g, &view));
        std::fs::write(g.join("mods/pack/extended.mod.json"), br#"{"enabled":true,"launcher":7}"#).unwrap();
        assert!(hosted(&g, &view), "on, and a launcher nlohmann cannot read as text: the catch counts it");
        std::fs::write(g.join("mods/pack/extended.mod.json"), br#"{"launcher":"EML","enabled":true}"#).unwrap();
        assert!(!hosted(&g, &view), "an EML package");
        std::fs::remove_dir_all(g).unwrap();
    }

    /// Rerun 2, finding 5: `..` relative to the game folder stops at the root of the game's drive, as on Windows.
    #[test]
    fn dot_dot_stops_at_the_game_drive_s_root() {
        let (t, g) = proton_game("r2-dotdot", "Enshrouded", &[crate::game::CLIENT_EXE], &[crate::CLIENT_APP_ID]);
        // S: is the game folder itself
        std::os::unix::fs::symlink(&g, t.join("lib/steamapps/compatdata/1203620/pfx/dosdevices/s:")).unwrap();
        std::fs::create_dir_all(t.join("lib/steamapps/common/sf-state")).unwrap();
        std::fs::write(t.join("lib/steamapps/common/sf-state/state.json"), APPLIED).unwrap();
        std::fs::write(g.join(crate::shroudforge::CONFIG_FILE), br#"{"paths":{"state":"..\\sf-state"}}"#).unwrap();
        let view = ProfileView { dropins_shroudforge: true, ..ProfileView::default() };
        assert!(!hosted(&g, &view), "S:\\sf-state, not the folder beside the game");
        std::fs::create_dir_all(g.join("sf-state")).unwrap();
        std::fs::write(g.join("sf-state/state.json"), APPLIED).unwrap();
        assert!(hosted(&g, &view));
        std::fs::remove_dir_all(t).unwrap();
    }

    /// Rerun 2, findings 8 and 10: EML's registry (mod-loader-base/src/registry/mod.rs `load_mod_at`, manifest.rs)
    /// takes a helper package without src/mod.lua; its runner needs that file only for patch or export. It refuses an
    /// `optional` that is no boolean, a version number above 64 bits and a byte order mark.
    #[test]
    fn eml_s_own_loader_takes_and_refuses_what_its_registry_does() {
        let d = tmp("r2-eml").join("mods");
        let pkg = |name: &str, manifest: &str, lua: Option<&str>| {
            let dir = d.join(name);
            std::fs::create_dir_all(dir.join("src")).unwrap();
            std::fs::write(dir.join("mod.json"), manifest).unwrap();
            if let Some(f) = lua { std::fs::write(dir.join("src").join(f), b"return {}").unwrap(); }
            dir
        };
        assert_eq!(eml_takes(&pkg("helper", r#"{"id":"helper","name":"H","version":"1.0.0"}"#, Some("util.lua"))), Ok(()));
        assert!(eml_takes(&pkg("patcher", r#"{"id":"p","name":"P","version":"1.0.0","capabilities":["patch"]}"#, Some("util.lua"))).is_err());
        assert!(eml_takes(&pkg("opt", r#"{"id":"o","name":"O","version":"1.0.0","dependencies":[{"id":"x","version":"^1","optional":"yes"}]}"#, None)).is_err());
        assert_eq!(eml_takes(&pkg("opt2", r#"{"id":"o","name":"O","version":"1.0.0","dependencies":[{"id":"x","version":"^1","optional":true}]}"#, None)), Ok(()));
        assert!(eml_takes(&pkg("big", r#"{"id":"b","name":"B","version":"1.0.18446744073709551616"}"#, None)).is_err());
        assert!(eml_takes(&pkg("bom", "\u{feff}{\"id\":\"b\",\"name\":\"B\",\"version\":\"1.0.0\"}", None)).is_err());
        assert!(eml_takes(&pkg(".hidden", r#"{"id":"h","name":"H","version":"1.0.0"}"#, None)).is_err());
        std::fs::remove_dir_all(d.parent().unwrap()).unwrap();
    }

    #[test]
    fn notes_say_what_runs() {
        let g = tmp("notes");
        std::fs::write(g.join("dbghelp.dll"), dll(b"EML_LOG_FILE_PATH")).unwrap();
        let l = find(&g, None);
        let mut view = ProfileView::default();
        for id in ["a", "b", "c"] { view.mods.push((ModKind::Eml, id.into(), true)); }
        view.mods.push((ModKind::Eml, "off".into(), false));
        for id in ["a", "b"] {
            std::fs::create_dir_all(g.join("mods").join(id)).unwrap();
            let m = crate::engine_mods::Marker { format: 1, kind: ModKind::Eml, id: id.into(), version: "1.0.0".into(), hash: "h".into() };
            crate::util::write_json_atomic(&g.join("mods").join(id).join(crate::engine_mods::MARKER), &m).unwrap();
        }
        let n = notes(&g, &l, Some(&view));
        assert_eq!(n.len(), 1);
        assert_eq!((n[0].enabled, n[0].copied.clone(), n[0].not_copied.clone()), (3, vec!["a".to_string(), "b".into()], vec!["c".to_string()]));
        assert!(n[0].text.starts_with("EML's own loader (dbghelp.dll) is installed: it runs the EML mods in mods/; 1 of this profile's 3 EML mod(s) are not in mods/"), "{}", n[0].text);
        assert!(n[0].text.contains("DLL override"));
        std::fs::create_dir_all(g.join("mods/c")).unwrap();
        let m = crate::engine_mods::Marker { format: 1, kind: ModKind::Eml, id: "c".into(), version: "1.0.0".into(), hash: "h".into() };
        crate::util::write_json_atomic(&g.join("mods/c").join(crate::engine_mods::MARKER), &m).unwrap();
        let n = notes(&g, &l, Some(&view));
        assert!(n[0].text.contains("Kiln copied this profile's 3 EML mod(s) into mods/ for it"), "{}", n[0].text);
        std::fs::remove_dir_all(g).unwrap();
    }
}
