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

/// `dinput8-chain.ini` `[Chain]` and `[EMF]` entries (relative to the game folder, EMF's rule).
fn emf_chained(game_dir: &Path) -> Vec<(String, PathBuf)> {
    let p = game_dir.join(crate::paths::game::EMF_CHAIN);
    let Ok(bytes) = std::fs::read(&p) else { return Vec::new() };
    if bytes.len() > crate::chain::MAX_BYTES { return Vec::new(); }
    let text = crate::chain::decode(&bytes);
    let mut out = Vec::new();
    for line in crate::chain::lines(&text) {
        let crate::chain::IniLine::Text(Some(section), value) = line else { continue };
        if section != "chain" && section != "emf" { continue; }
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
    // With drop-ins on, the adapter (native/plugins/shroudforge plugin.cpp `legacy_wanted`) also hosts ShroudForge when
    // ShroudForge's state file records a startup patch of an earlier start, so that ShroudForge can put the game data
    // back: then it runs the EML mods of its folder too, and Kiln's copies there need their switch.
    let hosts = profile.is_some_and(|p| p.enabled(ModKind::ShroudForge).next().is_some() || (p.dropins_shroudforge && {
        let dir = crate::shroudforge::mods_dir(game_dir);
        std::fs::read_dir(&dir).into_iter().flatten().flatten().any(|e| crate::shroudforge::is_sf_own_mod(&e.path()))
            || sf_patched_before(game_dir)
    }));
    if hosts && runtime && game_dir.join(plugin).is_file() {
        let dir = crate::shroudforge::mods_dir(game_dir);
        let folder = dir.strip_prefix(game_dir).map(|p| p.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| dir.display().to_string());
        out.push(OwnLoader { kind: ModKind::Eml, engine: Engine::ShroudForge, file: plugin.into(), via: Via::HostedShroudForge, folder,
            delivery: Delivery::Placed });
    }
    out
}

/// ShroudForge's state file (`<paths.state>/state.json`, by default `<game>/shroudforge/state.json`) records a startup
/// patch (`assets.status` applied, preparing or failed): the adapter's `legacy_wanted` hosts ShroudForge then.
fn sf_patched_before(game_dir: &Path) -> bool {
    let default = || game_dir.join("shroudforge/state.json");
    let file = match crate::shroudforge::read_effect_json(game_dir, &game_dir.join(crate::shroudforge::CONFIG_FILE)) {
        Ok(config) => match config.as_ref().and_then(|v| v.pointer("/paths/state")) {
            Some(serde_json::Value::String(p)) if !p.trim().is_empty() =>
                match crate::shroudforge::resolve_game_folder(game_dir, p.trim()) { Some(d) => d.join("state.json"), None => return false },
            _ => default(),
        },
        Err(_) => default(),
    };
    crate::shroudforge::read_effect_json(game_dir, &file).ok().flatten()
        .and_then(|v| v.pointer("/assets/status").and_then(|s| s.as_str()).map(str::to_owned))
        .is_some_and(|s| matches!(s.as_str(), "applied" | "preparing" | "failed"))
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
        ProfileView { mods, dropins_shroudforge: names_shroudforge(&p.dropins_file()) }
    }

    pub fn of_file(f: &crate::profile::ProfileFile) -> Self {
        let mut mods = Vec::new();
        for k in ModKind::ALL {
            for m in f.mods.folders(k).into_iter().flatten() { mods.push((k, m.id.clone(), m.enabled)); }
        }
        ProfileView { mods, dropins_shroudforge: names_shroudforge(&f.dropins) }
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
    /// EML ids the player's own folder in the loader's folder serves (a folder Kiln did not place, whose mod.json has the
    /// id): the loader runs that folder, Kiln places no copy and never writes into it (engine_mods::place).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own: Vec<String>,
    /// Ids of [`OwnLoaderNote::own`] that ShroudForge has switched off (their folder has no `extended.mod.json`, or its
    /// `enabled` is not true): ShroudForge does not run them, and activating the profile does not change that; the
    /// player switches them on in Kiln's menu (Mods, ShroudForge) or in ShroudForge's own Modloader.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_off: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`] that ShroudForge has switched off (EML mods while ShroudForge runs them: their
    /// copy has no `extended.mod.json`, or its `enabled` is not true): ShroudForge does not run them, and activating the
    /// profile again switches them on.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub off: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`] whose `extended.mod.json` targets the other side only (a server-only mod on a
    /// client, a client-only one on a dedicated server): ShroudForge does not run them here, and nothing needs doing.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub other_side: Vec<String>,
    /// Ids of [`OwnLoaderNote::copied`] whose package ShroudForge cannot read (it refuses their `mod.json` or
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

/// The player's own folder in `root` that serves the EML mod `id`: a folder without Kiln's marker whose mod.json names
/// the id (as kiln-eml and engine_mods::place find it). Other kinds: none.
fn own_folder_of(root: &Path, kind: ModKind, id: &str) -> Option<PathBuf> {
    if kind != ModKind::Eml { return None; }
    let serves = |dir: &Path| dir.is_dir() && crate::engine_mods::marker(dir).is_none()
        && std::fs::metadata(dir.join("mod.json")).is_ok_and(|m| m.is_file() && m.len() <= 1024 * 1024)
        && std::fs::read(dir.join("mod.json")).ok().and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
            .and_then(|v| v.get("id").and_then(|i| i.as_str()).map(|i| i.eq_ignore_ascii_case(id))) == Some(true);
    let named = root.join(id);
    if serves(&named) { return Some(named); }
    let mut dirs: Vec<PathBuf> = std::fs::read_dir(root).into_iter().flatten().flatten().map(|e| e.path()).collect();
    dirs.sort();
    dirs.into_iter().find(|p| serves(p))
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
        // the player's own folder there serves the rest of them, or nothing does (engine_mods::place copies none over it)
        let (own, not_copied): (Vec<String>, Vec<String>) = not_copied.into_iter().partition(|id| own_folder_of(&root, kind, id).is_some());
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
        // a player's own folder ShroudForge has off: Kiln never writes into it, so only the player can switch it on
        let own_off: Vec<String> = own.iter().filter(|_| sf_reads).filter(|id| matches!(
            own_folder_of(&root, kind, id).and_then(|dir| crate::shroudforge::switched_off(&dir, server)),
            Some(crate::shroudforge::SfOff::Switch(_)))).cloned().collect();
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
            let placed = n - own.len();
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
            text.push_str(&format!("{lead} run from your own folder(s) in {folder} ({}): Kiln places no copy of its own and never writes into a folder it did not place",
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
        if !other_side.is_empty() {
            text.push_str(&format!(". {} of them target the {} only, so ShroudForge does not run them {} ({}); nothing needs doing",
                other_side.len(), if server { "client" } else { "server" }, if server { "on this server" } else { "in this game" }, names(&other_side)));
        }
        if !unreadable.is_empty() {
            text.push_str(&format!(". ShroudForge cannot read {} of them, so it does not run them and no switch changes that ({}{}): a version \
                of the mod whose files ShroudForge reads does, or a profile without ShroudForge mods (Kiln then runs them itself)",
                unreadable.len(), if unreadable.len() == 1 { String::new() } else { format!("{}; ", names(&unreadable)) }, refused.unwrap_or_default()));
        }
        out.push(OwnLoaderNote { loader: l.clone(), enabled: n as u32, copied, not_copied, own, own_off, off, other_side, unreadable, text });
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
        assert!(e.text.contains("1 of this profile's 3 EML mod(s) run from your own folder(s) in mods/ (auto-loot-plus)"), "{}", e.text);
        assert!(e.text.contains("Switch them on in Kiln's in-game menu (Mods, ShroudForge) or in ShroudForge's own Modloader"), "{}", e.text);
        // switched on there (ShroudForge's Modloader writes the file): it runs, and nothing asks for a switch
        std::fs::write(own.join("extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{}}"#).unwrap();
        let n = notes(&g, &l, Some(&view));
        let e = n.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
        assert!(e.own_off.is_empty() && e.own == vec!["auto-loot-plus".to_string()], "{e:?}");
        assert!(!e.text.contains("switched off"), "{}", e.text);
        std::fs::remove_dir_all(g).unwrap();
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
