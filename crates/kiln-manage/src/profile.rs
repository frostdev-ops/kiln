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
///
/// How each reader takes it (docs/kiln/profiles.md "Drop-in folders"):
/// - kiln-core (`kiln`, `native`, `plugin`, `emf`, through `kiln.profile` `dropins()`) and kiln-eml (`eml`): `true`, or
///   a list naming the kind. A missing key is `false`.
/// - compat.h's engines (Greeneye, Flame, Shroudtopia, Enshwick, Ember) and kiln-shroudlink: the same rule since dev.22
///   (`true`, or a list naming the engine). Before dev.22 they ignored a list and read their folder whenever the profile
///   had no `mods.<engine>` list; [`Profile::dropins_file`] keeps that for profiles made before dev.22.
/// - kiln-shroudforge hosts ShroudForge (which runs its whole mods folder) when `mods.shroudforge` has an enabled entry,
///   or (from dev.22) when `dropins` is `true` or names `shroudforge` and that folder holds a ShroudForge package.
/// - kiln-enshwick starts Enshwick's loader for a hand-installed folder only when `dropins` is `true` or names
///   `enshwick`; once the loader runs (the profile has an Enshwick mod), it runs every folder in `<game>/mods`.
/// - kiln-set always reads `shrouded_engine_tweaks/mods` and does not read `dropins`.
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

impl Dropins {
    /// `true`, or a list naming `kind` (the rule of kiln-core and kiln-eml).
    pub fn names(&self, kind: ModKind) -> bool {
        match self {
            Dropins::All(b) => *b,
            Dropins::Kinds(v) => v.contains(&kind),
        }
    }

    /// `true` itself: every kind's folder.
    pub fn is_all(&self) -> bool {
        matches!(self, Dropins::All(true))
    }
}

/// The kinds whose plugin reads the profile's `dropins` for its own folder, in [`ModKind::ALL`] order: the switches the
/// launcher offers. `dropins: true` is written when a profile has every one of them on. SET does not read `dropins`
/// (see [`Dropins`]); Ember's mods do not run, so it has no switch. Enshrouded ModLoader's packages (dllmod) have none
/// either: a core before 0.2.0-dev.29.19 calls a profile whose `dropins` names a kind it does not know invalid, so the
/// list never names dllmod; its folder is read with `dropins: true` (every switch on) or without a profile.
pub const DROPIN_KINDS: [ModKind; 11] = [
    ModKind::Kiln,
    ModKind::Native,
    ModKind::Plugin,
    ModKind::Emf,
    ModKind::Eml,
    ModKind::ShroudForge,
    ModKind::Greeneye,
    ModKind::Flame,
    ModKind::Shroudtopia,
    ModKind::Enshwick,
    ModKind::Shroudlink,
];

/// Engine kinds whose plugin, before dev.22, read its own folder whenever the profile listed no mod of that engine,
/// whatever `dropins` said. A profile made before dev.22 keeps loading those folders ([`Profile::dropin_kinds`]).
const READ_WHEN_UNLISTED: [ModKind; 5] =
    [ModKind::Greeneye, ModKind::Flame, ModKind::Shroudtopia, ModKind::Shroudlink, ModKind::Ember];

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

/// `mods` of profile.json. The engine lists (eml ... ember) are left out of the file when empty, so a profile with Kiln
/// mods only is the same file older Kilns wrote. Each engine's plugin reads its own list from the profile's text.
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
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub eml: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub xhl: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub shroudforge: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub set: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub greeneye: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub flame: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub shroudtopia: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub enshwick: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub shroudlink: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub ember: Vec<FolderMod>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub dllmod: Vec<FolderMod>,
}

impl ProfileFileMods {
    /// The folder list of a kind (None for `plugin`, whose entries are DLLs).
    pub fn folders(&self, k: ModKind) -> Option<&Vec<FolderMod>> {
        Some(match k {
            ModKind::Plugin => return None,
            ModKind::Kiln => &self.kiln,
            ModKind::Native => &self.native,
            ModKind::Emf => &self.emf,
            ModKind::Eml => &self.eml,
            ModKind::Xhl => &self.xhl,
            ModKind::ShroudForge => &self.shroudforge,
            ModKind::Set => &self.set,
            ModKind::Greeneye => &self.greeneye,
            ModKind::Flame => &self.flame,
            ModKind::Shroudtopia => &self.shroudtopia,
            ModKind::Enshwick => &self.enshwick,
            ModKind::Shroudlink => &self.shroudlink,
            ModKind::Ember => &self.ember,
            ModKind::Dllmod => &self.dllmod,
        })
    }

    pub fn folders_mut(&mut self, k: ModKind) -> Option<&mut Vec<FolderMod>> {
        Some(match k {
            ModKind::Plugin => return None,
            ModKind::Kiln => &mut self.kiln,
            ModKind::Native => &mut self.native,
            ModKind::Emf => &mut self.emf,
            ModKind::Eml => &mut self.eml,
            ModKind::Xhl => &mut self.xhl,
            ModKind::ShroudForge => &mut self.shroudforge,
            ModKind::Set => &mut self.set,
            ModKind::Greeneye => &mut self.greeneye,
            ModKind::Flame => &mut self.flame,
            ModKind::Shroudtopia => &mut self.shroudtopia,
            ModKind::Enshwick => &mut self.enshwick,
            ModKind::Shroudlink => &mut self.shroudlink,
            ModKind::Ember => &mut self.ember,
            ModKind::Dllmod => &mut self.dllmod,
        })
    }
}

/// `<game>/kiln/profile.json` (and `kiln/profile.once.json`), format 1.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileFile {
    /// Enabled EML packages requiring hosted ShroudForge (default empty for older launchers).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub eml_runtime: Vec<String>,
    #[serde(default, skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub eml_runtime_blocked: std::collections::BTreeMap<String, String>,
    /// Enabled EML mods of which only part runs under Kiln, by Library id (and manifest id when that differs): what
    /// does not run, from the Library record (`ImportRecord::loader_part`: a modified EML loader in the mod's package
    /// carries the rest). kiln-eml logs it with the mod and adds it to the mod's line in its state file.
    #[serde(default, skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub eml_partial: std::collections::BTreeMap<String, String>,
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
    // (the launch plan a kiln-mode activation published for exactly this file: the core, kiln-sfi and
    // kiln-eml read it; crate::sfi_plan, crate::profile::sfi)
    #[cfg(feature = "sfi-scan")]
    #[serde(default, rename = "sfLaunchPlan", skip_serializing_if = "Option::is_none")]
    pub sf_launch_plan: Option<crate::sfi_plan::PlanRef>,
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

/// One list per [`ModKind`]. Older meta.json files have only the first four; the others default to empty.
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
    #[serde(default)]
    pub eml: Vec<ProfileMod>,
    #[serde(default)]
    pub xhl: Vec<ProfileMod>,
    #[serde(default)]
    pub shroudforge: Vec<ProfileMod>,
    #[serde(default)]
    pub set: Vec<ProfileMod>,
    #[serde(default)]
    pub greeneye: Vec<ProfileMod>,
    #[serde(default)]
    pub flame: Vec<ProfileMod>,
    #[serde(default)]
    pub shroudtopia: Vec<ProfileMod>,
    #[serde(default)]
    pub enshwick: Vec<ProfileMod>,
    #[serde(default)]
    pub shroudlink: Vec<ProfileMod>,
    #[serde(default)]
    pub ember: Vec<ProfileMod>,
    #[serde(default)]
    pub dllmod: Vec<ProfileMod>,
}

impl ProfileMods {
    /// The list of a kind.
    pub fn list(&self, k: ModKind) -> &Vec<ProfileMod> {
        match k {
            ModKind::Kiln => &self.kiln,
            ModKind::Native => &self.native,
            ModKind::Plugin => &self.plugin,
            ModKind::Emf => &self.emf,
            ModKind::Eml => &self.eml,
            ModKind::Xhl => &self.xhl,
            ModKind::ShroudForge => &self.shroudforge,
            ModKind::Set => &self.set,
            ModKind::Greeneye => &self.greeneye,
            ModKind::Flame => &self.flame,
            ModKind::Shroudtopia => &self.shroudtopia,
            ModKind::Enshwick => &self.enshwick,
            ModKind::Shroudlink => &self.shroudlink,
            ModKind::Ember => &self.ember,
            ModKind::Dllmod => &self.dllmod,
        }
    }

    pub fn list_mut(&mut self, k: ModKind) -> &mut Vec<ProfileMod> {
        match k {
            ModKind::Kiln => &mut self.kiln,
            ModKind::Native => &mut self.native,
            ModKind::Plugin => &mut self.plugin,
            ModKind::Emf => &mut self.emf,
            ModKind::Eml => &mut self.eml,
            ModKind::Xhl => &mut self.xhl,
            ModKind::ShroudForge => &mut self.shroudforge,
            ModKind::Set => &mut self.set,
            ModKind::Greeneye => &mut self.greeneye,
            ModKind::Flame => &mut self.flame,
            ModKind::Shroudtopia => &mut self.shroudtopia,
            ModKind::Enshwick => &mut self.enshwick,
            ModKind::Shroudlink => &mut self.shroudlink,
            ModKind::Ember => &mut self.ember,
            ModKind::Dllmod => &mut self.dllmod,
        }
    }
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
    /// `dropins` names the engine kinds too (written by dev.22 launchers). Without it (older profiles) a profile with
    /// all four Kiln kinds on loads every kind's drop-in folder, as before: [`Profile::dropin_kinds`].
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub dropins_engines: bool,
    #[serde(default)]
    pub data_dir: DataDirMode,
    #[serde(default)]
    pub server: Option<BoundServer>,
    /// Native mod ids whose dllmain DLLs load after Kiln and ShroudForge (chain.ini [late]) instead of in the proxy.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub late: Vec<String>,
}

impl Profile {
    /// The kinds whose drop-in folders this profile switches on, as the player sees them, in [`ModKind::ALL`] order.
    ///
    /// A profile from before `dropinsEngines` keeps what it loaded then. With all four Kiln kinds on that is every
    /// kind (older launchers wrote `dropins: true` for it). Otherwise it is its list, plus the folders the engine
    /// plugins read without asking `dropins`: Enshwick's always, and Greeneye's, Flame's, Shroudtopia's, shroudlink's
    /// and Ember's while the profile lists no mod of that engine.
    pub fn dropin_kinds(&self) -> Vec<ModKind> {
        if self.dropins_engines {
            return ModKind::ALL.into_iter().filter(|k| *k != ModKind::Xhl && self.dropins.contains(k)).collect();
        }
        if ModKind::CORE.iter().all(|k| self.dropins.contains(k)) {
            return ModKind::ALL.into_iter().filter(|k| *k != ModKind::Xhl).collect();
        }
        ModKind::ALL.into_iter().filter(|k| *k != ModKind::Xhl && (self.dropins.contains(k) || *k == ModKind::Enshwick
            || (READ_WHEN_UNLISTED.contains(k) && self.mods.list(*k).is_empty()))).collect()
    }

    /// profile.json `dropins` for this profile: `true` when every folder is on, `false` when none is, else the list of
    /// the kinds that are. Every plugin reads it the same way since dev.22 ([`Dropins`]), so the list names exactly the
    /// folders [`Profile::dropin_kinds`] shows. With `dropinsEngines`, "every" means every kind of [`DROPIN_KINDS`],
    /// and kinds without a switch are left out. A profile from before `dropinsEngines` gets `true` with all four Kiln
    /// kinds on, as older launchers wrote it.
    pub fn dropins_file(&self) -> Dropins {
        if !self.dropins_engines {
            if ModKind::CORE.iter().all(|k| self.dropins.contains(k)) {
                return Dropins::All(true);
            }
            // SET's plugin does not read `dropins`
            let on: Vec<ModKind> = self.dropin_kinds().into_iter().filter(|k| *k != ModKind::Set).collect();
            return if on.is_empty() { Dropins::All(false) } else { Dropins::Kinds(on) };
        }
        let on: Vec<ModKind> = DROPIN_KINDS.into_iter().filter(|k| self.dropins.contains(k)).collect();
        match on.len() {
            0 => Dropins::All(false),
            n if n == DROPIN_KINDS.len() => Dropins::All(true),
            _ => Dropins::Kinds(on),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileCounts {
    pub kiln: u32,
    pub native: u32,
    pub plugin: u32,
    pub emf: u32,
    #[serde(default)]
    pub eml: u32,
    #[serde(default)]
    pub xhl: u32,
    #[serde(default)]
    pub shroudforge: u32,
    #[serde(default)]
    pub set: u32,
    #[serde(default)]
    pub greeneye: u32,
    #[serde(default)]
    pub flame: u32,
    #[serde(default)]
    pub shroudtopia: u32,
    #[serde(default)]
    pub enshwick: u32,
    #[serde(default)]
    pub shroudlink: u32,
    #[serde(default)]
    pub ember: u32,
    #[serde(default)]
    pub dllmod: u32,
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
    /// Other engines' own loaders installed in this game and what they mean for the profile's mods of their engine
    /// (own_loaders::notes): each has a one-sentence `text` for the Play and profile pages. Empty: none installed.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_loaders: Vec<crate::own_loaders::OwnLoaderNote>,
    /// Files mods wrote into their placed folders that activation moved to `kiln/kept-mod-files` or put back from
    /// there (engine_mods::PlaceReport::kept_files): one sentence per mod. Empty: none.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub kept_files: Vec<String>,
    /// Safety repairs applied only to the game-side profile (saved profile and Library sources are unchanged), and the
    /// per-mod notes of the placement (engine_mods::PlaceReport::warnings: a ShroudForge setting value a new version
    /// refuses, earlier settings that could not be read).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
    /// Placed copies activation left in the game folder although the profile switched them off, removed them or wants
    /// another version (their code changed, or Kiln cannot inspect or carry what is in them): the same objects
    /// [`deactivate_report`] and uninstall return, `{path, reason, mayRun}`. Their own loader may still run them.
    /// Empty: none.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub retained: Vec<crate::engine_mods::RetainedPlacement>,
    /// The sentences of `warnings` about a player's own folder that may still run although the profile has its mod off
    /// (engine_mods::PlaceReport::safety_notes): the launcher raises these even with its notes about mod folders off.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub safety_notes: Vec<String>,
}

impl Activation {
    /// Two activations in one operation (a profile made or resumed, then written again with the added mods): what the
    /// earlier one kept or noted is kept in this one's lists. `retained` stays this one's: it is the game folder now.
    pub fn keep_notes_of(&mut self, earlier: Activation) {
        self.keep_notes(earlier.kept_files, earlier.warnings);
        for n in earlier.safety_notes { if !self.safety_notes.contains(&n) { self.safety_notes.push(n); } }
    }

    fn keep_notes(&mut self, kept_files: Vec<String>, warnings: Vec<String>) {
        for (mine, theirs) in [(&mut self.kept_files, kept_files), (&mut self.warnings, warnings)] {
            let later = std::mem::take(mine);
            *mine = theirs;
            for s in later { if !mine.contains(&s) { mine.push(s); } }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativePlanEntry {
    pub id: String,
    pub version: String,
    pub phase: crate::manifest::NativePhase,
    /// It runs. With a `reason`, it runs from the game folder: Kiln places the package there at activation and the
    /// game (or the package's own loader) loads it, not Kiln (game_files), and the reason says how.
    pub enabled: bool,
    /// Off: why it does not load. On: how it runs from the game folder. None: Kiln loads it, in its phase.
    pub reason: Option<String>,
}

/// What would load (resolver over the profile's Kiln mods for a side, plus the native order).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvePreview {
    pub side: RunSide,
    /// The main menu's load order and why the others wait or stay out. On a client a `side: "server"` mod is never
    /// "not loaded on a client" here: see `with_world`.
    pub kiln: Resolution,
    pub native: Vec<NativePlanEntry>,
    pub warnings: Vec<String>,
    /// Client: the Kiln mods that start with the player's own world (single-player, or friends joining it) instead of
    /// in the menu: `side: "server"` mods and the mods that need one (Kiln.Host ModManager.StartHosting), in the order
    /// they load then. Each is also in `kiln.rejected`, skipped, with [`WITH_WORLD`] as its reason. Empty on a server
    /// and when kiln.json `mods.hosted` is false ([`HOSTED_OFF`]).
    #[serde(default)]
    pub with_world: Vec<String>,
    /// The profile's switched-on EMF mods that a dependency keeps from loading, from their mod.json as kiln-emf checks
    /// them at the start ([`crate::emf_deps`]): what is missing, off or out of range, and how to get it. Without a game
    /// ([`resolve_preview_in`]) only the Library's mods count, not the game's emf/mods folder.
    #[serde(default)]
    pub emf: Vec<crate::emf_deps::EmfDepProblem>,
}

/// A client's `side: "server"` Kiln mod in the launcher: it starts with the player's own world, not in the menu, and
/// not in someone else's world (docs/kiln/client.md, "Mods and sides"). The game says `side 'server': loads while this
/// client hosts a world` (Kiln.Host ModManager.HostedReason).
pub const WITH_WORLD: &str = "Starts with your own world, when you play alone or friends join you. It does not run in someone else's world.";
/// The same with kiln.json `"mods": {"hosted": false}` in the game (Kiln.Host ModManager.HostedOffReason).
pub const HOSTED_OFF: &str = "Off in this game: its kiln.json turns off mods that start with your world (mods.hosted is false).";
/// A server mod that would not load when the player's world starts: this, then the resolver's reason.
pub const WORLD_FAILS: &str = "Will not start with your world: ";
/// The game's own words for the two (state/mods.json, kiln.log; managed/Kiln.Api/Mods.cs HostedReasons, a test keeps
/// them equal).
pub const GAME_HOSTED_WAITING: &str = "side 'server': loads while this client hosts a world";
pub const GAME_HOSTED_OFF: &str = "side 'server': not loaded on a client (kiln.json mods.hosted is false)";

/// kiln.json `"mods": {"hosted": false}` keeps a client's server mods off; anything else, a missing or unreadable file
/// too, leaves them on (Kiln.Host HostConfig.Read).
pub fn hosted_server_mods(game: &Path) -> bool {
    let Ok(text) = std::fs::read_to_string(game.join(crate::paths::game::KILN).join("kiln.json")) else { return true };
    let Ok(tree) = crate::jsonc::parse_tree(&text) else { return true };
    tree.get("mods").and_then(|m| m.get("hosted")).and_then(|h| h.as_bool()) != Some(false)
}

/// A Kiln mod of a profile that was built against a newer Kiln than a game's runtime (its kiln.mod.json `kilnBuild`): it may
/// use something that runtime doesn't have yet, and then fails to load until Kiln is updated.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewerKilnMod {
    pub id: String,
    pub version: String,
    /// The Kiln build it compiled against (`0.2.0-dev+20261106.0f0a3f6`).
    pub built_with: String,
}

/// The enabled Kiln mods of `profile` built against a newer Kiln than `runtime`. A mod without a stamp (built before Kiln
/// wrote one, or by hand) is never named.
pub fn built_for_newer(dirs: &DataDirs, profile: &Profile, runtime: &crate::manifest::KilnBuild) -> Vec<NewerKilnMod> {
    let mut out = Vec::new();
    for m in profile.mods.kiln.iter().filter(|m| m.enabled) {
        let Ok(folder) = crate::library::path_of(dirs, &lref(ModKind::Kiln, m)) else { continue };
        let Ok(man) = crate::manifest::read_kiln(&folder) else { continue };
        if let Some(b) = man.kiln_build.filter(|b| b.is_newer_than(runtime)) {
            out.push(NewerKilnMod { id: man.id, version: man.version, built_with: b.version });
        }
    }
    out
}

/// The runtime a game has, as a stamp to compare mods with: its receipt's bundle version and, when the launcher still has
/// that bundle, its `created` (the commit's time; without it two builds of one day can't be ordered).
pub fn runtime_build(dirs: &DataDirs, receipt: &crate::install::InstallReceipt) -> Option<crate::manifest::KilnBuild> {
    let created = crate::bundle::find(dirs, &receipt.runtime.version).ok().and_then(|b| b.created);
    crate::manifest::KilnBuild::from_parts(&receipt.runtime.version, created.as_deref())
}

/// Kiln.Mods.ApiVersion of this Kiln (managed/Kiln.Api/Mods.cs; a test keeps them equal).
pub const KILN_API: &str = "0.2.26";
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

/// `name`, or `name (2)`, `name (3)`... when a profile has that name already (trimmed, any case): an installed pack
/// never takes the name of a profile the player has (dev.30.2: a friend's "My mods" beside the player's own "My mods").
/// At most 128 characters, as [`create`] takes; the UI's `uniqueName` (launcher/src/crucible/code.ts) is the same rule.
pub fn unique_name(dirs: &DataDirs, name: &str) -> Result<String> {
    let name = name.trim();
    let mut taken = std::collections::BTreeSet::new();
    for e in std::fs::read_dir(dirs.profiles()).into_iter().flatten().flatten() {
        if let Ok(p) = get(dirs, &e.file_name().to_string_lossy()) {
            taken.insert(p.name.trim().to_lowercase());
        }
    }
    Ok(unique_among(name, &taken))
}

/// [`unique_name`] against names already lower-cased.
pub(crate) fn unique_among(name: &str, taken: &std::collections::BTreeSet<String>) -> String {
    let name = name.trim();
    let fit = |base: &str, suffix: &str| -> String {
        let room = 128usize.saturating_sub(suffix.chars().count());
        let base: String = base.chars().take(room).collect();
        format!("{}{suffix}", base.trim_end())
    };
    let first = fit(name, "");
    if first.is_empty() || !taken.contains(&first.to_lowercase()) {
        return first;
    }
    (2u32..)
        .map(|n| fit(name, &format!(" ({n})")))
        .find(|c| !taken.contains(&c.to_lowercase()))
        .unwrap_or(first)
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
    p.list(k)
}

fn lref(kind: ModKind, m: &ProfileMod) -> LibraryRef {
    LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() }
}

fn check_profile(p: &Profile) -> Result<()> {
    check_id(&p.id)?;
    if p.format != 1 {
        return Err(Error::new(ErrorCode::Unsupported, format!("profile format {} (this Kiln reads format 1)", p.format)));
    }
    if p.name.trim().is_empty() || p.name.chars().count() > 128 {
        return Err(Error::invalid("a profile needs a name of at most 128 characters"));
    }
    if p.generation == 0 || p.generation > i64::MAX as u64 {
        return Err(Error::new(ErrorCode::Schema, "profile generation must be an integer from 1 through 9223372036854775807"));
    }
    let mut problems = Vec::new();
    for k in ModKind::ALL {
        if list_of(&p.mods, k).len() > 4096 {
            problems.push(format!("{} exceeds 4096 entries", k.as_str()));
        }
        let mut seen = std::collections::BTreeSet::new();
        for m in list_of(&p.mods, k) {
            if !crate::manifest::is_mod_id(&m.id) || m.id.ends_with('\n') {
                problems.push(format!("{} mod id {:?} is not valid", k.as_str(), m.id));
            }
            if crate::hash::hash8(&m.hash).is_none() {
                problems.push(format!("{} {}: bad hash {:?}", k.as_str(), m.id, m.hash));
            }
            if m.hash.starts_with(crate::hash::KDH1_PREFIX) == (k == ModKind::Plugin) {
                problems.push(format!("{} {}: expected {}", k.as_str(), m.id, if k == ModKind::Plugin { "SHA-256" } else { "kdh1 hash" }));
            }
            if m.version.len() > 256 || m.version.trim() != m.version || m.version.contains(['/', '\\'])
                || crate::semver::SemVersion::parse(&m.version).is_err()
            {
                problems.push(format!("{} {}: bad semantic version {:?}", k.as_str(), m.id, m.version));
            }
            if !seen.insert(m.id.clone()) {
                problems.push(format!("{} {} is listed twice", k.as_str(), m.id));
            }
        }
    }
    if p.late.len() > 4096 {
        problems.push("late exceeds 4096 entries".to_string());
    }
    for id in p.late.iter().take(4096) {
        if !crate::manifest::is_mod_id(id) || id.ends_with('\n') {
            problems.push(format!("late mod id {id:?} is not valid"));
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
    check_profile(&p)?;
    Ok(p)
}

fn counts(p: &Profile) -> ProfileCounts {
    let n = |k| list_of(&p.mods, k).len() as u32;
    ProfileCounts {
        kiln: n(ModKind::Kiln),
        native: n(ModKind::Native),
        plugin: n(ModKind::Plugin),
        emf: n(ModKind::Emf),
        eml: n(ModKind::Eml),
        xhl: n(ModKind::Xhl),
        shroudforge: n(ModKind::ShroudForge),
        set: n(ModKind::Set),
        greeneye: n(ModKind::Greeneye),
        flame: n(ModKind::Flame),
        shroudtopia: n(ModKind::Shroudtopia),
        enshwick: n(ModKind::Enshwick),
        shroudlink: n(ModKind::Shroudlink),
        ember: n(ModKind::Ember),
        dllmod: n(ModKind::Dllmod),
    }
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
    if name.is_empty() || name.chars().count() > 128 {
        return Err(Error::invalid("a profile needs a name of at most 128 characters"));
    }
    let _one = one_at_a_time();
    // Rootless game pointers fall back to the local ID when no activation record exists. Do not make a foreign
    // pointer look local just by creating its meta.json. Rooted pointers already distinguish same-ID profiles;
    // existing local IDs are excluded independently below, so no ownership guess is needed here.
    let reserved: std::collections::BTreeSet<String> = crate::game::known(dirs)?.into_iter().filter_map(|install| {
        let file = read_game_profile(&install.path).ok().flatten()?;
        root_named(&file, &install).is_none().then_some(file.id)
    }).collect();
    let base = id_from_name(name);
    let mut id = base.clone();
    let mut n = 2;
    while dirs.profile(&id).exists() || reserved.contains(&id) {
        let suffix = format!("-{n}");
        id = format!("{}{suffix}", &base[..base.len().min(48 - suffix.len())]);
        n += 1;
    }
    let now = now_rfc3339();
    // `created` tells this profile from a deleted one with the same id (save): to the millisecond
    let created = created_now();
    let src = copy_from.map(|s| get(dirs, s)).transpose()?;
    let p = Profile {
        format: 1,
        id: id.clone(),
        name: name.to_string(),
        created,
        updated: now,
        generation: 1,
        notes: src.as_ref().map(|s| s.notes.clone()).unwrap_or_default(),
        mods: src.as_ref().map(|s| s.mods.clone()).unwrap_or_default(),
        dropins: src.as_ref().map(|s| s.dropins.clone()).unwrap_or_default(),
        dropins_engines: src.as_ref().is_some_and(|s| s.dropins_engines),
        data_dir: src.as_ref().map(|s| s.data_dir).unwrap_or_default(),
        server: src.as_ref().and_then(|s| s.server.clone()),
        late: src.as_ref().map(|s| s.late.clone()).unwrap_or_default(),
    };
    check_profile(&p)?;
    let dir = dirs.profile(&id);
    for d in ["config", "data"] {
        crate::durable::ensure_dir(&dir.join(d))?;
    }
    if let Some(s) = &src {
        let from = dirs.profile(&s.id).join("config");
        if from.is_dir() {
            crate::util::copy_tree(&from, &dir.join("config"))?;
            crate::durable::flush_contents(&dir.join("config"))?;
        }
    }
    crate::durable::json(&meta_path(dirs, &id), &p, &[dir.join("config"), dir.join("data")])?;
    Ok(p)
}

/// `2026-11-13T16:20:53.123Z`: when a profile was made, to the millisecond (RFC 3339 like every other time here).
fn created_now() -> String {
    let ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.subsec_millis()).unwrap_or(0);
    let secs = now_rfc3339();
    format!("{}.{ms:03}Z", secs.trim_end_matches('Z'))
}

/// A save reads, compares and writes a profile, and a delete or a create changes which profile an id names: one at a
/// time in this process, so a save that read a profile before it was deleted and made again never writes after that.
/// Two processes on one data folder (two launchers, or a launcher and kilnctl) are not ordered by this.
fn one_at_a_time() -> std::sync::MutexGuard<'static, ()> {
    static ONE: std::sync::Mutex<()> = std::sync::Mutex::new(());
    ONE.lock().unwrap_or_else(|e| e.into_inner())
}

/// Saves meta.json (generation + 1). Conflict when `profile.generation` is not the stored generation, or `created` is not
/// the stored profile's (it was deleted and made again under its id).
pub fn save(dirs: &DataDirs, profile: &Profile) -> Result<Profile> {
    check_profile(profile)?;
    let _one = one_at_a_time();
    let stored = get(dirs, &profile.id)?;
    // another profile under the same id: the one this save was made from was deleted and made again meanwhile (its
    // generation may well be the same), so nothing of it is written over the new one
    if stored.created != profile.created {
        return Err(Error::new(
            ErrorCode::Conflict,
            format!("profile {} was deleted and made again meanwhile; this change was for the deleted one, reload it", profile.id),
        ));
    }
    if stored.generation != profile.generation {
        return Err(Error::new(
            ErrorCode::Conflict,
            format!("profile {} changed meanwhile (generation {} on disk, {} here); reload it", profile.id, stored.generation, profile.generation),
        ));
    }
    #[cfg(test)]
    save_pause(&dirs.root);
    let mut p = profile.clone();
    p.generation = next_generation(p.generation)?;
    p.created = stored.created;
    p.updated = now_rfc3339();
    // durably (crate::durable): kept versions' records depend on the profile they follow
    let mut text = serde_json::to_string_pretty(&p)?;
    text.push('\n');
    crate::durable::replace(&meta_path(dirs, &p.id), text.as_bytes())?;
    Ok(p)
}

/// Moves the profile folder to `<data>/backups/profiles/<id>-<ts>/`.
pub fn delete(dirs: &DataDirs, id: &str) -> Result<()> {
    let _one = one_at_a_time();
    get(dirs, id)?;
    let base = format!("{id}-{}", ts_compact(crate::util::now_unix()));
    let mut to = dirs.backups().join("profiles").join(&base);
    // the same profile id deleted twice in one second (Pack Studio's test instance): a free name
    let mut n = 2;
    while to.exists() {
        to = dirs.backups().join("profiles").join(format!("{base}-{n}"));
        n += 1;
    }
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
        steam_messages: false,
        tests: None,
        kiln_build: None,
        boot: false,
        updates: n.updates.clone(),
        warnings: Vec::new(),
    }
}

/// A native mod that is another engine's own loader (SET's powrprof.dll ...): Kiln loads it, and that engine's plugin
/// then stands aside, so the engine's mods in the profile do not run through Kiln (player report r1110).
pub fn native_loader_warning(id: &str, engine: crate::engines::Engine, file: &str) -> String {
    let short = crate::engines::short_name(engine);
    format!("native mod {id} is {short}'s own loader ({file}), added before Kiln recognised it: Kiln loads it, so Kiln's {short} \
        plugin stands aside and the profile's {short} mods do not run through Kiln. Remove {id} from the profile and the Library; \
        Kiln runs {short} mods itself")
}

/// Old launchers imported SET's bundled proxy as a native mod. Do not run that accidental second loader from the
/// Library; leave both the Library source and saved profile untouched. Deliberate SET installs next to the game still
/// run as before (kiln-set's coexistence detector steps aside). Other engines may deliberately use their own loader.
fn skipped_native_loaders(dirs: &DataDirs, profile: &Profile) -> Vec<String> {
    profile.mods.native.iter().filter(|m| m.enabled).filter_map(|m| {
        let folder = crate::library::path_of(dirs, &lref(ModKind::Native, m)).ok()?;
        let (engine, file) = crate::engines::own_loader_in(&folder)?;
        (engine == crate::engines::Engine::Set).then(|| skipped_set_loader(&m.id, &file))
    }).collect()
}

/// A Kiln mod and an XHL-Core plugin of the same id both on (XHL ships Kiln builds of his mods under his plugins' ids):
/// where the game has XHL-Core (`game`, after chain.ini is written), Kiln loads the one and XHL-Core the other from the
/// game's mods folder, so the mod runs twice. One sentence per mod; none without XHL-Core.
fn xhl_twins(dirs: &DataDirs, profile: &Profile, game: &Path) -> Vec<String> {
    let twins: Vec<(ModKind, &str)> = profile.mods.kiln.iter().filter(|k| k.enabled).flat_map(|k| {
        [ModKind::Eml, ModKind::Xhl].into_iter().filter_map(move |kind| {
            let m = profile.mods.list(kind).iter().find(|m| m.enabled && m.id.eq_ignore_ascii_case(&k.id))?;
            let folder = crate::library::path_of(dirs, &lref(kind, m)).ok()?;
            crate::engine_mods::has_xhl_plugin_ini(&folder).then_some((kind, k.id.as_str()))
        })
    }).collect();
    if twins.is_empty() || !crate::engine_mods::xhl_host(game).available { return Vec::new(); }
    twins.into_iter().map(|(kind, id)| format!("kiln:{id} and {}:{id} are both on in this profile: Kiln runs the Kiln mod, and \
        XHL-Core runs the XHL-Core plugin of the same mod from the game's mods folder, so the mod runs twice. Switch one of them \
        off in the profile.", kind.as_str())).collect()
}

fn skipped_set_loader(id: &str, file: &str) -> String {
    format!("Skipped native mod {id}: {file} is Shrouded Engine Tweaks's own loader, not a mod; Kiln runs SET mods itself. \
        The Library source and saved profile are kept unchanged. Remove this native entry from the profile and import the \
        SET mod folders instead; to use SET's own loader deliberately, install it next to the game")
}

fn environment(side: RunSide) -> Environment {
    Environment { kiln: crate::VERSION.into(), api: KILN_API.into(), side, unavailable_features: Vec::new(), loaded: Vec::new(), hosting: false }
}

/// A client's menu resolution (`menu`) as the launcher shows it, with what happens when the player's own world starts
/// (`world`, resolved with `hosting`): Kiln.Host loads the server mods then, and the mods that failed for want of
/// one. Returns the ids that start with the world; their rejections become [`WITH_WORLD`]. A mod that would not load
/// then either says why (the two resolutions differ only where a server mod is involved). Without `world` (kiln.json
/// `mods.hosted` false) the server mods say [`HOSTED_OFF`].
fn with_world(menu: &mut Resolution, world: Option<&Resolution>, server_dirs: &std::collections::BTreeSet<String>) -> Vec<String> {
    let mut ids = Vec::new();
    for r in &mut menu.rejected {
        if r.state == ModState::Disabled {
            continue;
        }
        let side_skip = r.state == ModState::Skipped && server_dirs.contains(&r.dir);
        match world {
            None if side_skip => r.reason = HOSTED_OFF.into(),
            None => {}
            Some(w) => match w.rejected.iter().find(|x| x.dir == r.dir) {
                Some(x) if side_skip || x.reason != r.reason => (r.state, r.reason) = (x.state, format!("{WORLD_FAILS}{}", x.reason)),
                Some(_) => {}
                None if w.order.contains(&r.id) => {
                    (r.state, r.reason) = (ModState::Skipped, WITH_WORLD.into());
                    ids.push(r.id.clone());
                }
                None => {}
            },
        }
    }
    if let Some(w) = world {
        ids.sort_by_key(|id| w.order.iter().position(|x| x == id));
    }
    ids
}

/// Unbound preview (also kilnctl's default): both resolver environments use this launcher's [`crate::VERSION`],
/// not a runtime discovered in some other game. This is not an installed-runtime compatibility verdict.
pub fn resolve_preview(dirs: &DataDirs, profile: &Profile, side: RunSide) -> Result<ResolvePreview> {
    resolve_preview_for(dirs, profile, side, true)
}

/// [`resolve_preview`] in a game (None: no game), as the launcher asks for it: whether a client's server mods start with
/// the player's own world ([`hosted_server_mods`]), and EMF's dependencies over the game's emf/mods folder too.
/// Resolver-planned native mods use this game's receipt's runtime version for `native.json`'s `kiln` requirement.
/// Missing/unreadable/invalid receipts (including an invalid runtime version) keep the launcher-scoped native preview,
/// with a warning; a readable receipt is a snapshot, not a check of the installed DLLs or its age. Managed mods retain
/// their launcher-scoped environment/API; game-folder placements retain their separate planning rules.
pub fn resolve_preview_in(dirs: &DataDirs, profile: &Profile, side: RunSide, game: Option<&Path>) -> Result<ResolvePreview> {
    preview_with(dirs, profile, side, game.is_none_or(hosted_server_mods), game)
}

/// [`resolve_preview`] for a game whose kiln.json says whether a client's server mods start with the player's own
/// world (`hosted`: [`hosted_server_mods`]).
pub fn resolve_preview_for(dirs: &DataDirs, profile: &Profile, side: RunSide, hosted: bool) -> Result<ResolvePreview> {
    preview_with(dirs, profile, side, hosted, None)
}

/// The preview; EMF's dependencies (checked once) over the Library's mods, and with `game` its emf/mods folder too.
fn preview_with(dirs: &DataDirs, profile: &Profile, side: RunSide, hosted: bool, game: Option<&Path>) -> Result<ResolvePreview> {
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
    let mut starts_with_world = Vec::new();
    if side == RunSide::Client {
        // Kiln.Host says when a server mod loads (ModManager.LoadSet); the resolver's "not loaded on a client" is not it
        let server_dirs: std::collections::BTreeSet<String> = candidates.iter().filter(|c| c.manifest.as_ref().is_some_and(|m| m.side == crate::manifest::Side::Server))
            .map(|c| c.dir.clone()).collect();
        let world = if hosted {
            // ModManager.StartHosting keeps the menu's mods running: ordering edges cannot reorder them. It resolves
            // the skipped server mods first, then retries failed dependents of the server mods that resolved.
            let mut env = Environment { hosting: true, ..environment(side) };
            let manifests = |ids: &[String]| candidates.iter().filter_map(|c|
                c.manifest.as_ref().filter(|m| ids.contains(&m.id)).cloned()).collect::<Vec<_>>();
            env.loaded = manifests(&kiln.order);
            let servers: Vec<_> = candidates.iter().filter(|c| kiln.rejected.iter().any(|r|
                r.dir == c.dir && r.state == ModState::Skipped && server_dirs.contains(&r.dir))).cloned().collect();
            let mut world = crate::resolver::resolve(&servers, &env)?;
            let mut dependents: std::collections::BTreeSet<String> = world.order.iter().cloned().collect();
            let mut retry_dirs = std::collections::BTreeSet::new();
            loop {
                let mut changed = false;
                for c in &candidates {
                    if !retry_dirs.contains(&c.dir)
                        && kiln.rejected.iter().chain(&world.rejected).any(|r| r.dir == c.dir && r.state == ModState::Failed)
                        && c.manifest.as_ref().is_some_and(|m| m.dependencies.iter().any(|d| dependents.contains(&d.id)))
                    {
                        retry_dirs.insert(c.dir.clone());
                        dependents.insert(c.id());
                        changed = true;
                    }
                }
                if !changed { break; }
            }
            let retry: Vec<_> = candidates.iter().filter(|c| retry_dirs.contains(&c.dir) && !servers.iter().any(|s| s.dir == c.dir)).cloned().collect();
            env.loaded.extend(manifests(&world.order));
            let retried = crate::resolver::resolve(&retry, &env)?;
            world.order.extend(retried.order);
            world.rejected.extend(retried.rejected);
            Some(world)
        } else { None };
        starts_with_world = with_world(&mut kiln, world.as_ref(), &server_dirs);
    }
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
        if let Some(plan) = crate::game_files::plan(&folder) {
            // the game loads it from the game folder, not Kiln: on, its reason says how; unless activation places none
            // of its files (game_files::activate)
            let (enabled, reason) = match &plan.problem {
                Some(problem) => (false, format!("not placed in the game folder: {problem}")),
                None => (true, crate::game_files::preview_reason(&plan)),
            };
            native.push(NativePlanEntry { id: m.id.clone(), version: m.version.clone(), phase, enabled, reason: Some(reason) });
            continue;
        }
        if let Some((engine, file)) = crate::engines::own_loader_in(&folder) {
            if engine == crate::engines::Engine::Set {
                let reason = skipped_set_loader(&m.id, &file);
                warnings.push(reason.clone());
                native.push(NativePlanEntry { id: m.id.clone(), version: m.version.clone(), phase, enabled: false, reason: Some(reason) });
                continue;
            }
            warnings.push(native_loader_warning(&m.id, engine, &file));
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
    let mut native_env = environment(side);
    if !ncands.is_empty() {
        if let Some(game) = game {
            match crate::install::read_receipt(game) {
                Ok(Some(r)) if crate::semver::SemVersion::try_parse(&r.runtime.version).is_some() => {
                    native_env.kiln = r.runtime.version;
                }
                receipt => {
                    let why = match receipt {
                        Ok(None) => "no install receipt".into(),
                        Ok(Some(_)) => "the install receipt has an invalid runtime version".into(),
                        Err(e) => format!("the install receipt could not be read: {}", e.message),
                    };
                    warnings.push(format!("Native compatibility preview uses launcher Kiln {}: {why}; installed runtime compatibility is unknown.", crate::VERSION));
                }
            }
        }
    }
    let nres = crate::resolver::resolve(&ncands, &native_env)?;
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
    for k in ModKind::ALL.into_iter().filter(|k| *k == ModKind::Plugin || k.is_engine()) {
        for m in list_of(&profile.mods, k) {
            let p = crate::library::path_of(dirs, &lref(k, m)).ok();
            if !p.as_ref().is_some_and(|p| p.exists()) {
                warnings.push(format!("{} {} {} is not in the library", k.as_str(), m.id, m.version));
            }
        }
    }
    let emf = crate::emf_deps::profile_problems(dirs, profile, side, game);
    Ok(ResolvePreview { side, kiln, native: ordered, warnings, with_world: starts_with_world, emf })
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

/// The folder name of a mod's copy under `kiln/profiles/<pid>/<kind>/` in Copy mode: the mod's id, except for an EMF Lua
/// mod that runs from a folder of another name in emf/mods (`game_path`, the player's own: engine_mods::game_path_of).
/// kiln-emf tells EMF's lua mod the copy's folder name, and the lua mod reads the Lua files from `emf/mods/<that name>`,
/// so the copy takes the name of the folder that is there. A name another mod of the list has stays the id.
fn copy_name(profile: &Profile, kind: ModKind, m: &ProfileMod, game_path: Option<&Path>) -> String {
    let name = game_path.filter(|_| kind == ModKind::Emf).and_then(|p| p.file_name()).map(|n| n.to_string_lossy().into_owned());
    match name {
        Some(n) if n != m.id && !n.starts_with('.') && crate::engine_mods::is_game_folder_name(&n)
            && !profile.mods.list(kind).iter().any(|o| o.id.eq_ignore_ascii_case(&n)) => n,
        _ => m.id.clone(),
    }
}

fn copy_rel(pid: &str, kind: ModKind, id: &str) -> String {
    format!("{}/{pid}/{}/{id}", COPY_ROOT_REL, kind.as_str())
}

/// `<game>/kiln/profiles` relative to `<game>/kiln/`.
const COPY_ROOT_REL: &str = "profiles";

/// The reader revision (native/core/src/profile.h KILN_PROFILE_READER) from which a core reads Copy mode's paths into
/// a game folder outside kiln/: `../mods/<folder>` in mods.eml, `../greeneye/<id>` in mods.greeneye.
pub const PROFILE_READER_GAME_MODS: u32 = 2;

/// The reader revision from which a core reads `../mods/<id>` in mods.flame (Kiln's copy of a Flame mod in the folder it
/// reads its own files from, engine_mods::FLAME_MODS). A core of revision 2 calls such a profile invalid.
pub const PROFILE_READER_FLAME_MODS: u32 = 3;

/// The reader revision from which a core knows mods.dllmod and its Copy-mode path `../Mods/EnshroudedModLoader/<id>`
/// (Kiln's copy of a package for Enshrouded ModLoader, engine_mods::run_folder). An older core skips mods.dllmod whole.
pub const PROFILE_READER_DLLMOD: u32 = 4;

/// The reader revision a Copy-mode path into the game folder outside kiln/ needs for mods of `kind`.
fn reader_for_game_path(kind: ModKind) -> u32 {
    match kind {
        ModKind::Flame => PROFILE_READER_FLAME_MODS,
        ModKind::Dllmod => PROFILE_READER_DLLMOD,
        // no core reads `../emf/mods/<id>` (native/core/src/profile.cpp): Copy mode names an EMF Lua mod's copy under
        // kiln/profiles/, and EMF's lua mod reads the Lua files from Kiln's copy in emf/mods, the folder of that name
        ModKind::Emf => u32::MAX,
        _ => PROFILE_READER_GAME_MODS,
    }
}

/// The profile.json reader revision of the runtime in the game: what the kiln-runtime.json of the bundle its install
/// receipt names declares (`profileReader`). 1 when it declares none (a runtime from before), and when the launcher
/// cannot tell (no receipt, a core file that is not the receipt's, or no such bundle in `<data>/runtime/`): an older
/// form is always read, a newer one is not.
fn runtime_profile_reader(dirs: &DataDirs, install: &GameInstall) -> u32 {
    let Ok(Some(receipt)) = crate::install::read_receipt(&install.path) else { return 1 };
    // believed only while the core in the game is the one the receipt lists (an older runtime unzipped by hand over a
    // launcher install keeps the newer receipt)
    let core = "kiln/kiln-core.dll";
    let listed = receipt.files.iter().find(|f| f.path == core);
    if !listed.is_some_and(|f| crate::hash::sha256_file(&install.path.join(core)).is_ok_and(|(h, _)| h == f.sha256)) {
        return 1;
    }
    crate::bundle::find(dirs, &receipt.runtime.version).ok().and_then(|b| crate::bundle::read(&b.file).ok())
        .and_then(|m| m.profile_reader).unwrap_or(1)
}

/// Renders the game-side file for an install (no writes): game paths, fingerprint, generation.
pub fn render(dirs: &DataDirs, profile: &Profile, install: &GameInstall, mode: PathMode) -> Result<ProfileFile> {
    render_routed(dirs, profile, install, mode, &crate::sfi_routing::route_of(&install.path), false)
}

/// [`render`] on the route of the ShroudForge-format mods the operation decided once ([`crate::sfi_routing`]).
/// `planned`: Kiln's interpreter runs this start from a launch plan whose data writer is kiln-eml, so an EML package that
/// needs ShroudForge's runtime stays switched on for kiln-eml (it applies the package's startup data as the plan admits
/// it, the interpreter runs the rest); without a plan main's rule stands (switched off, with its note).
pub(crate) fn render_routed(dirs: &DataDirs, profile: &Profile, install: &GameInstall, mode: PathMode, route: &crate::sfi_routing::Route,
                            planned: bool) -> Result<ProfileFile> {
    check_profile(profile)?;
    let pid = &profile.id;
    let mut mods = ProfileFileMods::default();
    // Copy mode names a game folder outside kiln/ (../mods/<folder>, ../greeneye/<id>, ../mods/<id> for Flame) only for a
    // core that reads that form in that list, and only under its folder name rule: every older core calls the whole
    // profile invalid and loads no mods, so those entries keep the copy under kiln/profiles/ (sync_copies makes it), as
    // before the form existed.
    let reader = std::cell::OnceCell::new();
    let outside_kiln_ok = |k: ModKind, placed: &Path| *reader.get_or_init(|| runtime_profile_reader(dirs, install)) >= reader_for_game_path(k)
        && placed.file_name().and_then(|n| n.to_str()).is_some_and(crate::engine_mods::is_game_folder_name);
    for k in ModKind::ALL.into_iter().filter(|k| *k != ModKind::Plugin) {
        for m in list_of(&profile.mods, k) {
            let folder = crate::library::path_of(dirs, &lref(k, m))?;
            // a native mod whose files activation places in the game folder (a loader the game loads itself, Ultimate
            // ASI Loader and .asi plugins): kiln-core does not load it, on or off (an .asi package's native.json names no
            // DLL, which kiln-core would call broken)
            if k == ModKind::Native && crate::game_files::plan(&folder).is_some() {
                continue;
            }
            // ShroudForge and SET read their mods from their own folder in the game: the copy placed there
            // (their plugins take the list as "this profile uses the engine"; copy mode keeps the relative copy path)
            // An EML mod a native DLL reads in the game's mods folder runs from the copy placed there, so the DLL and
            // the Lua read the same settings (engine_mods::game_folder_of)
            let game_path = crate::engine_mods::game_path_of(dirs, k, m, &install.path);
            let placed = game_path.clone().or_else(|| crate::engine_mods::placed_folder_routed(k, &install.path, &m.id, route));
            // XHL-Core named dinput8.dll runs from Kiln's copy under its own name, in kiln/chain/ (Copy mode too)
            let xhl_core = k == ModKind::Native && m.enabled && crate::engine_mods::xhl_core_chain_dlls(&folder).is_some();
            let path = match placed {
                _ if xhl_core => game_or_copy(&crate::engine_mods::xhl_core_copy(&install.path, &m.id), install, mode,
                    || crate::engine_mods::xhl_core_copy_rel(&m.id))?,
                Some(placed) if mode != PathMode::Copy => game_or_copy(&placed, install, mode, String::new)?,
                Some(placed) if game_path.is_some() && outside_kiln_ok(k, &placed) => format!("../{}", placed.strip_prefix(&install.path)
                    .map_err(|_| Error::invalid("EML game folder is outside the install"))?.to_string_lossy().replace('\\', "/")),
                _ if mode == PathMode::Copy && crate::engine_mods::run_folder(k).is_some() => run_folder_copy_rel(profile, install, k, m, &folder)?,
                _ => game_or_copy(&folder, install, mode, || copy_rel(pid, k, &copy_name(profile, k, m, game_path.as_deref())))?,
            };
            let enabled = m.enabled && !(k == ModKind::Native && crate::engines::own_loader_in(&folder)
                .is_some_and(|(engine, _)| engine == crate::engines::Engine::Set));
            let fm = FolderMod { id: m.id.clone(), version: Some(m.version.clone()), path, hash: Some(m.hash.clone()), enabled };
            if let Some(list) = mods.folders_mut(k) {
                list.push(fm);
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
    let dropins = profile.dropins_file();
    let view = crate::own_loaders::ProfileView::of_profile(profile).with_library(dirs, profile);
    let eml_runtime = view.eml_runtime.clone();
    let loaders = crate::own_loaders::find_routed(&install.path, Some(&view), route);
    crate::own_loaders::check_runtime_eml_loader(&loaders, &eml_runtime)?;
    let mut eml_runtime_blocked = std::collections::BTreeMap::new();
    for id in &eml_runtime {
        if let Some(m) = mods.eml.iter_mut().find(|m| &m.id == id) {
            if crate::own_loaders::shroudforge_runs_eml(&loaders) {
                // Compatibility signal for adapters predating emlRuntime: this profile uses SF for this package.
                if !mods.shroudforge.iter().any(|s| s.id == *id && s.enabled) {
                    let mut engine = m.clone();
                    engine.enabled = true;
                    mods.shroudforge.push(engine);
                }
                // (a kiln start's plan names kiln-eml as its data writer: kiln-eml keeps the package)
                if planned && route.runs() {
                    continue;
                }
                // SF reads its placed copy/switch, not this list. Disable kiln-eml's route even for old runners:
                // if an older adapter fails before reserving the writer, no data-only fallback may run.
                m.enabled = false;
            } else {
                m.enabled = false;
                let note = if crate::own_loaders::shroudforge_env_off() { crate::own_loaders::RUNTIME_ENV_OFF_NOTE.to_owned() }
                    else { "needs ShroudForge's runtime, which is not running: the ShroudForge component is missing or unavailable; its data was not applied, so the game is as without the mod. Install/enable ShroudForge and activate again".to_owned() };
                eml_runtime_blocked.insert(id.clone(), note);
            }
        }
    }
    let mut eml_partial = std::collections::BTreeMap::new();
    for m in profile.mods.list(ModKind::Eml).iter().filter(|m| m.enabled) {
        let r = crate::library::LibraryRef { kind: ModKind::Eml, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        let Ok(rec) = crate::library::record(dirs, &r) else { continue };
        let Some(note) = rec.loader_part else { continue };
        if let Some(own) = rec.manifest_id.filter(|own| *own != m.id) {
            eml_partial.insert(own, note.clone());
        }
        eml_partial.insert(m.id.clone(), note);
    }
    Ok(ProfileFile {
        eml_runtime,
        eml_runtime_blocked,
        eml_partial,
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
        #[cfg(feature = "sfi-scan")]
        sf_launch_plan: None,
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

pub(crate) fn running_process_with(
    dir: &Path,
    selected: &str,
    mut find: impl FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>,
) -> Result<Option<crate::proc::ProcessInfo>> {
    // A client and server can be installed together and read the same engine folders.
    let names = [selected, crate::game::CLIENT_EXE, crate::game::SERVER_EXE];
    for (i, exe) in names.iter().enumerate() {
        if !names[..i].contains(exe) && let Some(p) = find(dir, exe)? {
            return Ok(Some(p));
        }
    }
    Ok(None)
}

fn running_process(install: &GameInstall) -> Result<Option<crate::proc::ProcessInfo>> {
    running_process_with(&install.path, &install.exe, crate::proc::find_exe)
}

pub(crate) fn refuse_running(install: &GameInstall) -> Result<()> {
    if let Some(p) = running_process(install)? {
        return Err(Error::new(ErrorCode::GameRunning, format!("{} is running (pid {}); quit it first", p.name, p.pid))
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

// Unlike kdh1, include dot files and empty directories: neither is disposable player data. Refuse links.
fn same_copy_tree(a: &Path, b: &Path) -> Result<bool> {
    crate::engine_mods::refuse_symlink_path(a, a)?;
    crate::engine_mods::refuse_symlink_path(b, b)?;
    let names = |p: &Path| -> Result<std::collections::BTreeSet<std::ffi::OsString>> {
        std::fs::read_dir(p)?.map(|e| e.map(|e| e.file_name()).map_err(Error::from)).collect()
    };
    let entries = names(a)?;
    if entries != names(b)? { return Ok(false); }
    for name in entries {
        let (a, b) = (a.join(&name), b.join(&name));
        crate::engine_mods::refuse_symlink_path(&a, &a)?;
        crate::engine_mods::refuse_symlink_path(&b, &b)?;
        let (am, bm) = (std::fs::symlink_metadata(&a)?, std::fs::symlink_metadata(&b)?);
        if am.is_dir() && bm.is_dir() {
            if !same_copy_tree(&a, &b)? { return Ok(false); }
        } else if !(am.is_file() && bm.is_file() && crate::hash::sha256_file(&a)? == crate::hash::sha256_file(&b)?) {
            return Ok(false);
        }
    }
    Ok(true)
}

// The Library's files are all in `dst`, unchanged, and nothing in `dst` is a link: whatever else the folder holds, the
// mod or the player wrote there (settings, logs). Such a copy is still the copy of that version and is used as it is;
// a fresh sibling at every activation would leave the mod's settings behind each time.
fn holds_copy_tree(src: &Path, dst: &Path) -> Result<bool> {
    fn no_links(p: &Path) -> Result<bool> {
        for e in std::fs::read_dir(p)? {
            let e = e?.path();
            let m = std::fs::symlink_metadata(&e)?;
            if m.file_type().is_symlink() || !(m.is_dir() || m.is_file()) || (m.is_dir() && !no_links(&e)?) { return Ok(false); }
        }
        Ok(true)
    }
    fn within(src: &Path, dst: &Path) -> Result<bool> {
        for e in std::fs::read_dir(src)? {
            let name = e?.file_name();
            let (a, b) = (src.join(&name), dst.join(&name));
            let am = std::fs::symlink_metadata(&a)?;
            let Ok(bm) = std::fs::symlink_metadata(&b) else { return Ok(false) };
            if am.is_dir() {
                if !(bm.is_dir() && within(&a, &b)?) { return Ok(false); }
            } else if !(am.is_file() && bm.is_file() && crate::hash::sha256_file(&a)? == crate::hash::sha256_file(&b)?) {
                return Ok(false);
            }
        }
        Ok(true)
    }
    crate::engine_mods::refuse_symlink_path(src, src)?;
    crate::engine_mods::refuse_symlink_path(dst, dst)?;
    Ok(std::fs::symlink_metadata(dst)?.is_dir() && no_links(dst)? && within(src, dst)?)
}

// A fallback that a mod or player changed is not disposable, even while an older runtime still needs a copy.
// A copy that still holds every Library file unchanged is used as it is, with what was written beside them. Any other
// (another version, a changed shipped file, a link) is left alone and a free sibling is used instead; the unused-copy
// pass keeps the old folder in place and reports its exact path.
fn run_folder_copy_rel(profile: &Profile, install: &GameInstall, kind: ModKind, m: &ProfileMod, src: &Path) -> Result<String> {
    let parent = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id).join(kind.as_str());
    crate::engine_mods::refuse_symlink_path(&install.path, &parent)?;
    for n in 0..128 {
        // A leading dot cannot be a Library mod id, so this cannot take another selected mod's destination.
        let name = if n == 0 { m.id.clone() } else { format!(".kiln-copy-{}-{n}", m.id) };
        let rel = copy_rel(&profile.id, kind, &name);
        let dst = install.path.join(crate::paths::game::KILN).join(&rel);
        match std::fs::symlink_metadata(&dst) {
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(rel),
            Err(e) => return Err(io_at(&dst, "checking")(e)),
            Ok(_) if holds_copy_tree(src, &dst).unwrap_or(false) => return Ok(rel),
            Ok(_) => {},
        }
    }
    Err(Error::new(ErrorCode::Refused, format!("{}: no free fallback copy folder; existing files were kept", parent.display())))
}

/// Copy mode: `<game>/kiln/profiles/<pid>/<kind>/<id>/` hold verified copies of the enabled mods; unchanged copies
/// are kept. Flame/Greeneye use a free sibling when that copy changed and retain unused changed copies. Returns the
/// number of folders copied.
fn sync_copies(dirs: &DataDirs, profile: &Profile, install: &GameInstall, warnings: &mut Vec<String>) -> Result<u32> {
    let base = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id);
    let mut copied = 0;
    // A Greeneye or Flame mod whose current copy is in <game>/greeneye/ or <game>/mods/ runs from there when the core
    // reads `../greeneye/<id>` or `../mods/<id>` in that list (render): no copy here. An older core gets this copy, as
    // the profile then names it.
    let reader = std::cell::OnceCell::new();
    let runs_placed = |k: ModKind, m: &ProfileMod| crate::engine_mods::run_folder(k).is_some()
        && crate::engine_mods::game_path_of(dirs, k, m, &install.path).is_some()
        && *reader.get_or_init(|| runtime_profile_reader(dirs, install)) >= reader_for_game_path(k);
    for k in ModKind::ALL {
        let kdir = base.join(k.as_str());
        let wanted = list_of(&profile.mods, k).iter().filter(|m| m.enabled && !runs_placed(k, m)).map(|m| {
            let rel = if crate::engine_mods::run_folder(k).is_some() {
                run_folder_copy_rel(profile, install, k, m, &crate::library::path_of(dirs, &lref(k, m))?)?
            } else {
                let game_path = (k == ModKind::Emf).then(|| crate::engine_mods::game_path_of(dirs, k, m, &install.path)).flatten();
                copy_rel(&profile.id, k, &copy_name(profile, k, m, game_path.as_deref()))
            };
            Ok((m, install.path.join(crate::paths::game::KILN).join(rel)))
        }).collect::<Result<Vec<_>>>()?;
        for e in std::fs::read_dir(&kdir).into_iter().flatten().flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            if !wanted.iter().any(|(_, dst)| *dst == e.path()) {
                if crate::engine_mods::run_folder(k).is_some() {
                    let unchanged = crate::engine_mods::refuse_symlink_path(&install.path, &e.path()).is_ok()
                        && list_of(&profile.mods, k).iter().find(|m| m.id == name)
                            .and_then(|m| crate::library::path_of(dirs, &lref(k, m)).ok()
                                .filter(|src| crate::hash::dirhash(src).is_ok_and(|h| h == m.hash)))
                            .is_some_and(|src| same_copy_tree(&src, &e.path()).unwrap_or(false));
                    if !unchanged {
                        warnings.push(format!("{}:{name}: kept the unused profile copy at {}: its files are changed or unverified", k.as_str(), e.path().display()));
                        continue;
                    }
                }
                let _cook = match hold_copy(k, &e.path(), &name) {
                    Ok(held) => held,
                    Err(why) => {
                        warnings.push(format!("{}:{name}: kept the unused profile copy at {}: {}", k.as_str(), e.path().display(), why.message));
                        continue;
                    }
                };
                let _ = std::fs::remove_dir_all(e.path());
            }
        }
        for (m, dst) in wanted {
            let src = crate::library::path_of(dirs, &lref(k, m))?;
            let mut _cook = None;
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
                    if crate::hash::sha256_file(&src).is_ok_and(|(h, _)| h != m.hash) {
                        // the player changed the Library's DLL: which mod, and the way to take it over
                        return Err(crate::library::changed_error(&lref(k, m), src.parent().unwrap_or(&src)));
                    }
                    return Err(Error::new(ErrorCode::HashMismatch, format!("{}: the copy does not match {}", target.display(), m.hash)));
                }
            } else {
                let rec = crate::library::record(dirs, &lref(k, m))?;
                let output = if crate::engine_mods::run_folder(k).is_none() {
                    crate::library::own_output(&src, &rec)?.unwrap_or_default()
                } else { Vec::new() };
                let matches = |folder: &Path| -> Result<bool> {
                    if crate::engine_mods::run_folder(k).is_some() { Ok(crate::hash::of_mod(k, folder)? == m.hash) }
                    else { crate::library::matches_import(folder, &rec, &output) }
                };
                if crate::engine_mods::run_folder(k).is_some() {
                    crate::engine_mods::refuse_symlink_path(&install.path, &dst)?;
                    match std::fs::symlink_metadata(&dst) {
                        // the Library entry is the profile's version and the copy still holds all of it
                        Ok(_) if crate::hash::dirhash(&src)? == m.hash && holds_copy_tree(&src, &dst)? => {
                            let _ = crate::util::make_copy_writable(&src, &dst);
                            continue;
                        }
                        Ok(_) => return Err(Error::new(ErrorCode::Refused,
                            format!("{}: the fallback copy changed while preparing it; its files were kept", dst.display()))),
                        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {},
                        Err(e) => return Err(io_at(&dst, "checking")(e)),
                    }
                } else {
                    // a Kiln mod's copy the player cooked (assets/cooked) is still that mod: kept with its cooked files
                    // (the Library's files in it made writable where an earlier copy kept one read-only: the mod runs
                    // from this copy; what the mod or the player added stays as it is)
                    if dst.is_dir() && matches(&dst).unwrap_or(false) {
                        let _ = crate::util::make_copy_writable(&src, &dst);
                        continue;
                    }
                    // changed in the game folder since Kiln copied it: never thrown away without a word (changed_copy)
                    let changed = if dst.is_dir() { changed_copy(dirs, k, m, &src, &dst) } else { None };
                    if changed.as_ref().is_some_and(|c| c.settings_only) {
                        let _ = crate::util::make_copy_writable(&src, &dst);
                        continue; // only its own settings files changed: the player's, kept as they are
                    }
                    _cook = hold_copy(k, &dst, &m.id)?;
                    match changed {
                        Some(c) => warnings.push(set_aside_copy(install, profile, k, m, &dst, &c)?),
                        None => { let _ = std::fs::remove_dir_all(&dst); }
                    }
                }
                crate::util::copy_tree(&src, &dst)?;
                // copy_tree keeps a read-only Library file read-only (an entry an older Kiln imported from a folder)
                crate::util::make_copy_writable(&src, &dst).map_err(io_at(&dst, "making writable"))?;
                if !matches(&dst)? {
                    if matches(&src).is_ok_and(|same| !same) {
                        // the player changed the files in the Library folder: which mod, and the way to take it over
                        return Err(crate::library::changed_error(&lref(k, m), &src));
                    }
                    return Err(Error::new(ErrorCode::HashMismatch, format!("{}: the copy does not match the library entry {}", dst.display(), m.hash)));
                }
            }
            copied += 1;
        }
    }
    Ok(copied)
}

/// How a copy under `<game>/kiln/profiles/` differs from what Kiln copied there ([`changed_copy`]).
struct ChangedCopy {
    /// The files that are not as the Library has them (changed, missing, or added and not the mod's own output), relative.
    files: Vec<String>,
    /// The same version, and only its kind's settings files changed (engine_mods::settings_files: EML's config.txt and
    /// config.ini, SET's settings.json) or the mod added its own output: the player's settings, kept as they are, as
    /// placement keeps them in a placed copy.
    settings_only: bool,
}

/// Whether Kiln's copy `dst` of the profile entry `m` (Library folder `src`) was changed in the game folder: None when it
/// holds exactly a Library version of this mod (the profile's, or the version an earlier activation copied there, which
/// an update replaces), or when it cannot be read (it is replaced as before). Some(what changed) otherwise, compared
/// with the version its manifest names when the Library has it, else with the profile's.
fn changed_copy(dirs: &DataDirs, k: ModKind, m: &ProfileMod, src: &Path, dst: &Path) -> Option<ChangedCopy> {
    crate::engine_mods::refuse_symlink_path(dst, dst).ok()?;
    let versions: Vec<_> = crate::library::records(dirs, k).into_iter().filter(|(_, rec)| rec.id == m.id).collect();
    for (folder, rec) in &versions {
        let output = crate::library::own_output(folder, rec).ok().flatten().unwrap_or_default();
        if crate::library::matches_import(dst, rec, &output).unwrap_or(false) { return None; }
    }
    let records = |dir: &Path| crate::hash::dir_records(dir).ok()
        .map(|r| r.into_iter().map(|f| (f.path, f.sha256)).collect::<std::collections::BTreeMap<_, _>>());
    let now = records(dst)?;
    // the version the copy says it is, when the Library has it: what changed since Kiln copied it there
    let own = crate::engine_mods::identity(dst).1.or_else(|| crate::manifest::read_kiln(dst).ok().map(|x| x.version));
    let base = versions.iter().find(|(_, rec)| own.as_deref() == Some(rec.version.as_str())).map(|(f, _)| f.as_path()).unwrap_or(src);
    let listed = records(base)?;
    let mut files: Vec<String> = listed.iter().filter(|(rel, hash)| now.get(*rel) != Some(hash)).map(|(rel, _)| rel.clone()).collect();
    let added: Vec<String> = now.keys().filter(|rel| !listed.contains_key(*rel)).cloned().collect();
    let output = added.iter().all(|rel| crate::engine_mods::is_runtime_output(rel));
    let settings = |rel: &String| crate::engine_mods::settings_files(k).iter().any(|s| s.eq_ignore_ascii_case(rel));
    let settings_only = base == src && output && files.iter().all(settings);
    files.extend(added.into_iter().filter(|rel| !crate::engine_mods::is_runtime_output(rel)));
    files.sort();
    Some(ChangedCopy { files, settings_only })
}

/// A copy [`changed_copy`] found changed, about to be replaced from the Library: moved aside to
/// `<game>/kiln/backups/profile-copy-<profile>-<kind>-<id>-<time>/` (nothing the player changed is lost), and one
/// sentence for the activation's notes (launcher.log and the launcher's notice) that says what was replaced and where it
/// is. ShroudForge and SET never read this copy (they run the one placed in their own folder,
/// engine_mods::placed_folder), so what changed there never applied; the sentence says where settings do apply.
fn set_aside_copy(install: &GameInstall, profile: &Profile, k: ModKind, m: &ProfileMod, dst: &Path, c: &ChangedCopy) -> Result<String> {
    let base = install.path.join(crate::paths::game::BACKUPS)
        .join(format!("profile-copy-{}-{}-{}-{}", profile.id, k.as_str(), m.id, ts_compact(crate::util::now_unix())));
    let aside = next_copy_name(&base);
    crate::engine_mods::refuse_symlink_path(&install.path, &aside)?;
    std::fs::create_dir_all(aside.parent().expect("parent")).map_err(io_at(&aside, "creating"))?;
    std::fs::rename(dst, &aside).map_err(io_at(&aside, "moving the changed profile copy to"))?;
    prune_profile_copies(aside.parent().expect("parent"), &format!("profile-copy-{}-{}-{}-", profile.id, k.as_str(), m.id));
    let shown: Vec<&str> = c.files.iter().take(3).map(String::as_str).collect();
    let what = match c.files.len() {
        0 => String::new(),
        n if n <= 3 => format!(" ({})", shown.join(", ")),
        n => format!(" ({} and {} more)", shown.join(", "), n - 3),
    };
    let placed = crate::engine_mods::placed_folder(k, &install.path, &m.id);
    Ok(match (placed, k.engine().map(|e| e.name())) {
        (Some(placed), Some(engine)) => format!("{}:{}: Kiln's copy in {} was changed in the game folder{what}. {engine} never reads that copy (it runs \
            {}), so those changes did not apply. Kiln replaced it with the Library's files and kept the changed copy in {}. Change the mod's settings \
            in the game's Mods page or in the Kiln launcher: both change the copy {engine} runs, and profile updates keep them.",
            k.as_str(), m.id, dst.display(), placed.display(), aside.display()),
        _ => format!("{}:{}: its copy in {} was changed in the game folder{what}. Kiln replaced it with the Library's files and kept the changed \
            copy in {}.", k.as_str(), m.id, dst.display(), aside.display()),
    })
}

/// The name for a copy set aside in the second `base` names: `base`, else `base-<n>` after the highest `n` in use. Never
/// a lower name that [`prune_profile_copies`] freed: a newer copy under it would sort as the oldest and be deleted at
/// once, with the notice naming a folder that is gone (more than three set aside within one second).
fn next_copy_name(base: &Path) -> PathBuf {
    let name = base.file_name().and_then(|n| n.to_str()).unwrap_or_default();
    let last = base.parent().and_then(|d| std::fs::read_dir(d).ok()).into_iter().flatten().flatten()
        .filter_map(|e| {
            let rest = e.file_name().into_string().ok()?.strip_prefix(name)?.to_string();
            if rest.is_empty() { Some(1) } else { rest.strip_prefix('-')?.parse::<u32>().ok().filter(|n| *n >= 2) }
        })
        .max()
        .unwrap_or(0);
    (last + 1..).map(|n| if n == 1 { base.to_path_buf() } else { crate::util::with_suffix(base, &format!("-{n}")) })
        .find(|p| std::fs::symlink_metadata(p).is_err()).expect("a free name")
}

/// How many changed copies of one profile's mod [`set_aside_copy`] keeps in `<game>/kiln/backups/`: a mod that changes
/// its own files in its copy at every run would otherwise leave one more at every activation.
const PROFILE_COPY_BACKUPS: usize = 3;

/// Deletes all but the newest [`PROFILE_COPY_BACKUPS`] folders named `<prefix><yyyymmddThhmmssZ>` or
/// `<prefix><yyyymmddThhmmssZ>-<n>` in `dir` (the names [`set_aside_copy`] gives one mod's copies; another mod's id that
/// starts with this one's is never one of them). Best effort: a folder that cannot be read or deleted stays.
fn prune_profile_copies(dir: &Path, prefix: &str) {
    fn stamp(rest: &str) -> Option<(String, u32)> {
        let (ts, n) = match rest.split_once('-') { Some((ts, n)) => (ts, n.parse::<u32>().ok().filter(|n| *n >= 2)?), None => (rest, 1) };
        let b = ts.as_bytes();
        (b.len() == 16 && b[8] == b'T' && b[15] == b'Z' && b[..8].iter().chain(&b[9..15]).all(u8::is_ascii_digit)).then(|| (ts.to_string(), n))
    }
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    let mut found: Vec<((String, u32), PathBuf)> = rd.flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_dir()))
        .filter_map(|e| { let name = e.file_name().into_string().ok()?; Some((stamp(name.strip_prefix(prefix)?)?, e.path())) })
        .collect();
    found.sort();
    let old = found.len().saturating_sub(PROFILE_COPY_BACKUPS);
    for (_, p) in found.into_iter().take(old) {
        let _ = std::fs::remove_dir_all(p);
    }
}

/// A Kiln mod's copy in the game is the folder its cook writes (and sets aside, and puts back): it is replaced or
/// removed only with that cook's lock held ([`crate::state::lock_cook_folder`]), refused (Busy) while a cook of it runs.
/// None: not a Kiln mod's copy, or not there.
fn hold_copy(k: ModKind, dst: &Path, id: &str) -> Result<Option<crate::state::HeldLock>> {
    if k != ModKind::Kiln || std::fs::symlink_metadata(dst).is_err() {
        return Ok(None);
    }
    crate::state::lock_cook_folder(dst, std::time::Duration::ZERO, &|| false,
        &format!("Kiln is cooking {id} in this game: wait until that is done, then try again.")).map(Some)
}

/// What [`copy_back`] did.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ConfigSync {
    /// Files taken from the game's copy into the data folder.
    pub copied: u64,
    /// One sentence for each file that changed on both sides (or differed while there was no record yet): which copy
    /// was kept and where the other one is now. For the activation's warnings and the launcher's log.
    pub conflicts: Vec<String>,
}

/// One file of the game's config copy as Kiln last saw it ([`ConfigSyncRecord`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SyncedFile {
    sha256: String,
    size: u64,
    /// Nanoseconds since the Unix epoch (0: unknown). For a reader of the record: the hash decides.
    mtime_ns: u64,
}

/// Copy mode: the game's config copy (`<game>/kiln/profiles/<pid>/config`) as Kiln left it, at the last copy into the
/// game or copy back: a file the game changed since is one whose hash differs from it. One per profile and game, in
/// `<data>/profiles/<pid>/config-sync/<game id>.json` ([`config_sync_record`]).
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ConfigSyncRecord {
    format: u32,
    /// By the path below the config folder, '/'-separated.
    files: std::collections::BTreeMap<String, SyncedFile>,
}

/// Where copy mode records the game's config copy of a profile ([`ConfigSyncRecord`]).
pub fn config_sync_record(dirs: &DataDirs, profile_id: &str, install_id: &str) -> PathBuf {
    dirs.profile(profile_id).join("config-sync").join(format!("{install_id}.json"))
}

/// A config folder's regular files by their '/'-separated path below it (with that path), and its folders.
type ConfigTree = (std::collections::BTreeMap<String, PathBuf>, Vec<PathBuf>);

/// What is below `root`; links are refused, as `util::copy_tree` does. Nothing when `root` is not a folder.
fn config_tree(root: &Path) -> Result<ConfigTree> {
    fn walk(root: &Path, rel: &Path, out: &mut ConfigTree) -> Result<()> {
        let dir = root.join(rel);
        for e in std::fs::read_dir(&dir).map_err(io_at(&dir, "reading"))? {
            let e = e?;
            let ft = e.file_type()?;
            let r = rel.join(e.file_name());
            if ft.is_symlink() {
                return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not copied", e.path().display())));
            } else if ft.is_dir() {
                out.1.push(r.clone());
                walk(root, &r, out)?;
            } else {
                let key = r.components().map(|c| c.as_os_str().to_string_lossy()).collect::<Vec<_>>().join("/");
                out.0.insert(key, r);
            }
        }
        Ok(())
    }
    let mut out = ConfigTree::default();
    if root.is_dir() {
        walk(root, Path::new(""), &mut out)?;
    }
    Ok(out)
}

fn synced_file(path: &Path) -> Result<SyncedFile> {
    let (sha256, size) = crate::hash::sha256_file(path)
        .map_err(|e| Error::new(e.code, format!("{}: {}", path.display(), e.message)))?;
    let mtime_ns = std::fs::metadata(path).and_then(|m| m.modified()).ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_nanos() as u64);
    Ok(SyncedFile { sha256, size, mtime_ns })
}

fn write_config_sync_record(dirs: &DataDirs, profile_id: &str, install: &GameInstall, game_cfg: &Path) -> Result<()> {
    let (files, _) = config_tree(game_cfg)?;
    let files = files.into_iter().map(|(k, rel)| Ok((k, synced_file(&game_cfg.join(rel))?))).collect::<Result<_>>()?;
    let record = ConfigSyncRecord { format: 1, files };
    crate::util::write_json_atomic(&config_sync_record(dirs, profile_id, &install.id), &record)
}

/// `<file>.conflict-<time>` beside `file`, a name not taken yet.
fn conflict_name(file: &Path) -> PathBuf {
    let base = crate::util::with_suffix(file, &format!(".conflict-{}", ts_compact(crate::util::now_unix())));
    (1..).map(|n| if n == 1 { base.clone() } else { crate::util::with_suffix(&base, &format!("-{n}")) })
        .find(|p| std::fs::symlink_metadata(p).is_err()).expect("a free name")
}

/// Copy mode: the game-side config folder (`<game>/kiln/profiles/<pid>/config`, where Kiln writes) back into
/// `<data>/profiles/<pid>/config`, file by file, against the record of what the game's copy held when Kiln last copied
/// it in or back ([`config_sync_record`]). Called before an activation and after the game exits. The launcher's Configs
/// editor writes the data folder's copy, so neither side simply wins:
/// - a file the game changed (or created) since, and the data folder did not: the game's is copied back;
/// - a file only the data folder changed (a launcher edit): the data folder's stays, and goes into the game at the next
///   activation;
/// - a file both changed (to different contents): the newer by its modification time stays (the data folder's when they
///   are as old), the other is kept beside it in the data folder as `<name>.conflict-<time>`, and a sentence says so;
/// - deletions: a file missing from the game's copy is never deleted from the data folder (Kiln copies it in again; a
///   copy into the game that stopped halfway leaves files missing too). A file deleted from the data folder (the
///   launcher's "reset a whole file") stays deleted while the game's copy is unchanged; one the game changed since
///   comes back;
/// - without a record (the first copy back after an update to this rule, a record that cannot be read), and when the
///   data folder has no config folder for the profile at all (removed by hand: no "reset" of every file): a file the
///   two folders hold differently is a conflict, decided by time as above; a file only one holds is kept.
///
/// The record is updated to the game's copy as it is now. Nothing is copied while the game's `profile.json` is another
/// data folder's profile of the same id ([`game_profile_owner`]): that copy was written for the other one.
pub fn copy_back(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<ConfigSync> {
    let from = install.path.join(crate::paths::game::COPY_PROFILES).join(profile_id).join("config");
    if !from.is_dir() || foreign_same_id(dirs, profile_id, install) {
        return Ok(ConfigSync::default());
    }
    let to = dirs.profile(profile_id).join("config");
    let record_path = config_sync_record(dirs, profile_id, &install.id);
    let record = crate::util::read_json::<ConfigSyncRecord>(&record_path).ok().flatten()
        .filter(|r| r.format == 1 && to.is_dir());
    let (game, game_dirs) = config_tree(&from)?;
    let (data, _) = config_tree(&to)?;
    std::fs::create_dir_all(&to).map_err(io_at(&to, "creating"))?;
    for d in &game_dirs {
        let p = to.join(d);
        std::fs::create_dir_all(&p).map_err(io_at(&p, "creating"))?;
    }
    let mut out = ConfigSync::default();
    let mut seen = std::collections::BTreeMap::new();
    for (key, rel) in &game {
        let (g_path, d_path) = (from.join(rel), to.join(rel));
        let g = synced_file(&g_path)?;
        let d = if data.contains_key(key) { Some(synced_file(&d_path)?) } else { None };
        let base = record.as_ref().map(|r| r.files.get(key));
        let take = match (&d, base) {
            (Some(d), _) if d.sha256 == g.sha256 => false,
            // only the game has it: created there, or deleted from the data folder while the game's copy stayed
            (None, Some(Some(b))) => b.sha256 != g.sha256,
            (None, _) => true,
            (Some(d), Some(b)) => {
                let game_changed = b.is_none_or(|b| b.sha256 != g.sha256);
                let data_changed = b.is_none_or(|b| b.sha256 != d.sha256);
                if !game_changed {
                    false
                } else if !data_changed {
                    true
                } else {
                    let why = if b.is_some() { BOTH_CHANGED } else { BOTH_CREATED };
                    conflict(&mut out, profile_id, key, why, &g_path, &d_path)?
                }
            }
            // no record: which side changed is not known
            (Some(_), None) => conflict(&mut out, profile_id, key, NO_RECORD, &g_path, &d_path)?,
        };
        if take {
            if let Some(parent) = d_path.parent() {
                std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
            }
            std::fs::copy(&g_path, &d_path).map_err(io_at(&d_path, "copying to"))?;
            out.copied += 1;
        }
        seen.insert(key.clone(), g);
    }
    crate::util::write_json_atomic(&record_path, &ConfigSyncRecord { format: 1, files: seen })?;
    Ok(out)
}

const BOTH_CHANGED: &str = "changed in the game and in the launcher since Kiln last copied it";
const BOTH_CREATED: &str = "is new in the game and in the launcher, with different contents";
const NO_RECORD: &str = "differs between the game and the launcher, and Kiln has no record of its last copy yet";

/// Both copies of a config file changed: the newer stays (the data folder's when they are as old), the other is kept
/// beside it in the data folder. True when the game's is the newer (the caller copies it over the data folder's).
fn conflict(out: &mut ConfigSync, profile_id: &str, key: &str, why: &str, game: &Path, data: &Path) -> Result<bool> {
    let modified = |p: &Path| std::fs::metadata(p).and_then(|m| m.modified()).map_err(io_at(p, "reading the time of"));
    let game_newer = modified(game)? > modified(data)?;
    let aside = conflict_name(data);
    if game_newer {
        std::fs::rename(data, &aside).map_err(io_at(&aside, "keeping the launcher's copy as"))?;
    } else {
        std::fs::copy(game, &aside).map_err(io_at(&aside, "keeping the game's copy as"))?;
    }
    let (kept, other) = if game_newer { ("the game's", "the launcher's") } else { ("the launcher's", "the game's") };
    out.conflicts.push(format!(
        "Profile \"{profile_id}\": config {key} {why}. Kept {kept} copy (the newer one); {other} copy is saved as {}.",
        aside.display()
    ));
    Ok(game_newer)
}

/// Who a game's `kiln/profile.json` belongs to, seen from one data folder ([`game_profile_owner`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "owner")]
pub enum GameProfileOwner {
    /// No `profile.json`: the legacy layout.
    None,
    /// A profile of this data folder.
    Here { id: String },
    /// Another data folder's profile (another launcher, a `KILN_DATA_DIR`, a launcher before dev.26 at the default root
    /// after a move): left alone. `root`: the data folder its paths name, in Z and native mode.
    Elsewhere { id: String, root: Option<PathBuf> },
}

/// The data root a profile file's absolute paths name: `configDir` is `<root>/profiles/<id>/config` in Z and native
/// mode. None in copy mode (relative paths) and when it is not that shape.
fn root_named(file: &ProfileFile, install: &GameInstall) -> Option<PathBuf> {
    let cfg = file.config_dir.as_deref()?;
    if !crate::winpath::is_absolute_game_path(cfg) && !cfg.starts_with('/') {
        return None;
    }
    let host = crate::winpath::to_host(cfg, install).ok()?;
    let last: Vec<String> = host.components().rev().take(3).map(|c| c.as_os_str().to_string_lossy().into_owned()).collect();
    if last != ["config", file.id.as_str(), "profiles"] {
        return None;
    }
    host.parent()?.parent()?.parent().map(Path::to_path_buf)
}

/// A game's profile file is this data folder's: the profile is here, and the file names this data folder (Z and
/// native mode: its `configDir`), or it is the one this data folder last wrote into the game (`installs/<id>.active.json`:
/// the same id, generation and fingerprint). In copy mode the paths name no folder: the record decides, and without a
/// record the id alone does (as before dev.26).
pub(crate) fn file_is_here(dirs: &DataDirs, install: &GameInstall, file: &ProfileFile) -> bool {
    if get(dirs, &file.id).is_err() {
        return false;
    }
    let rec = crate::util::read_json::<Activation>(&activation_record(dirs, install)).ok().flatten();
    let recorded = rec.as_ref().is_some_and(|a| {
        a.profile_id == file.id && a.generation == file.generation && file.fingerprint.clone().unwrap_or_default() == a.fingerprint
    });
    match root_named(file, install) {
        Some(root) => {
            let style = crate::data_folder::PathStyle::of(&crate::paths::DataEnv { windows: cfg!(windows), ..Default::default() });
            let same = |a: &Path, b: &Path| {
                crate::data_folder::PathParts::of(&crate::data_folder::canonical(a), style)
                    .same(&crate::data_folder::PathParts::of(&crate::data_folder::canonical(b), style))
            };
            same(&root, &dirs.root) || recorded
        }
        None => recorded || rec.is_none(),
    }
}

fn foreign_same_id(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> bool {
    read_game_profile(&install.path).ok().flatten().is_some_and(|f| f.id == profile_id && !file_is_here(dirs, install, &f))
}

/// Who the game's `kiln/profile.json` belongs to ([`file_is_here`]'s rule), so the launcher can say "another data
/// folder's profile is active in this game" instead of taking it over.
pub fn game_profile_owner(dirs: &DataDirs, install: &GameInstall) -> Result<GameProfileOwner> {
    let Some(file) = read_game_profile(&install.path)? else { return Ok(GameProfileOwner::None) };
    Ok(if file_is_here(dirs, install, &file) {
        GameProfileOwner::Here { id: file.id }
    } else {
        GameProfileOwner::Elsewhere { root: root_named(&file, install), id: file.id }
    })
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

/// chain.ini as the profile wants it: [profile], its late natives and the compat rules (compat::plan). Returns true
/// when it wrote, and what the rules decided about the load order (compat::Plan::order_notes: one sentence each, for
/// the activation's notes and the launcher's log).
fn write_chain_plan(dirs: &DataDirs, profile: &Profile, install: &GameInstall, file: &ProfileFile) -> Result<(bool, Vec<String>)> {
    let entries = crate::compat::profile_entries(dirs, profile, install, file)?;
    let plan = crate::compat::plan(dirs, profile, install, &entries);
    Ok((crate::chain::write_profile_plan(&install.path, &plan.chain)?, plan.order_notes()))
}

/// Prepares everything a profile file needs on the game side (path mode, copies, config dir, native.json) and
/// renders it. Shared by activation and play-once profiles.
// (the route the operation decided once, and whether a launch plan is to be published: render_routed)
fn prepare(dirs: &DataDirs, profile: &Profile, install: &GameInstall, route: &crate::sfi_routing::Route, planned: bool)
    -> Result<(ProfileFile, PathMode, u32, Vec<String>)> {
    let mut mode = crate::game::path_mode(install, dirs)?;
    let file = match render_routed(dirs, profile, install, mode, route, planned) {
        Ok(f) => f,
        Err(e) if e.code == ErrorCode::Unsupported && mode == PathMode::Z => {
            mode = PathMode::Copy; // a path Windows cannot name: the whole profile is copied
            render_routed(dirs, profile, install, mode, route, planned)?
        }
        Err(e) => return Err(e),
    };
    let mut copied = 0;
    let mut warnings = Vec::new();
    if mode == PathMode::Copy {
        // the game's config copy of another data folder's profile with this id is that folder's: kept aside, not
        // copied back here and not deleted
        let foreign = foreign_same_id(dirs, &profile.id, install);
        warnings.extend(copy_back(dirs, &profile.id, install)?.conflicts);
        copied = sync_copies(dirs, profile, install, &mut warnings)?;
        let game_cfg = config_host_dir(dirs, profile, install, mode);
        if foreign && game_cfg.is_dir() {
            let base = install.path.join(crate::paths::game::BACKUPS).join(format!("profile-config-{}-{}", profile.id, ts_compact(crate::util::now_unix())));
            let aside = (1..).map(|n| if n == 1 { base.clone() } else { crate::util::with_suffix(&base, &format!("-{n}")) })
                .find(|p| std::fs::symlink_metadata(p).is_err()).expect("a free name");
            std::fs::create_dir_all(aside.parent().expect("parent")).map_err(io_at(&aside, "creating"))?;
            std::fs::rename(&game_cfg, &aside).map_err(io_at(&aside, "moving the other data folder's config to"))?;
            warnings.push(format!(
                "The game held the configs of another data folder's profile \"{}\": they were kept in {}, not mixed into this one.",
                profile.id,
                aside.display()
            ));
        } else {
            let _ = std::fs::remove_dir_all(&game_cfg);
        }
        let data_cfg = dirs.profile(&profile.id).join("config");
        std::fs::create_dir_all(&data_cfg).map_err(io_at(&data_cfg, "creating"))?;
        // the switches go into the data folder's copy first (copy_back brought the game's native.json there before), so
        // the game's copy is the data folder's exactly and the record says what Kiln put there
        write_native_switches(&data_cfg, profile)?;
        crate::util::copy_tree(&data_cfg, &game_cfg)?;
        write_config_sync_record(dirs, &profile.id, install, &game_cfg)?;
        if profile.data_dir == DataDirMode::Profile {
            let d = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id).join("data");
            std::fs::create_dir_all(&d).map_err(io_at(&d, "creating"))?;
        }
    } else {
        for d in ["config", "data"] {
            let p = dirs.profile(&profile.id).join(d);
            std::fs::create_dir_all(&p).map_err(io_at(&p, "creating"))?;
        }
        write_native_switches(&config_host_dir(dirs, profile, install, mode), profile)?;
    }
    Ok((file, mode, copied, warnings))
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

fn next_generation(generation: u64) -> Result<u64> {
    generation.checked_add(1).filter(|g| *g <= i64::MAX as u64)
        .ok_or_else(|| Error::new(ErrorCode::Schema, "profile generation is exhausted"))
}

// (kiln mode: an activation, a refresh and a play-once start of a game whose kiln.json selects Kiln's
// interpreter, journaled, with the launch plan their profile file pins; main's routes get only the settling
// of an unfinished one first and the clean-up of plans nothing pins any more after)
#[cfg(feature = "sfi-scan")]
#[path = "sfi_profile.rs"]
pub mod sfi;

/// Without the `sfi-scan` feature: main's routes, nothing of kiln mode to settle or clean up.
#[cfg(not(feature = "sfi-scan"))]
mod sfi {
    use super::*;
    pub(super) struct Decision {
        pub(super) route: crate::sfi_routing::Route,
    }
    impl Decision {
        pub(super) fn of(_: &GameInstall) -> Decision {
            Decision { route: crate::sfi_routing::Route::Legacy(crate::sfi_routing::Legacy::Off) }
        }
    }
    pub(super) enum After {
        Activated,
        Once,
        Deactivated,
    }
    pub(super) fn settle(_: &DataDirs, _: &GameInstall) -> Result<Vec<String>> {
        Ok(Vec::new())
    }
    pub(super) fn after(_: &DataDirs, _: &GameInstall, _: &Decision, _: After) {}
}

/// The activation flow (contracts.md): refuse while running, path mode, copy, chain.ini [profile], profile.json.
pub fn activate(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<Activation> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    // (the route of the ShroudForge-format mods, decided once for the whole activation; an unfinished
    // kiln-mode activation is settled before anything here changes a copy)
    let sfi = sfi::Decision::of(install);
    #[cfg(feature = "sfi-scan")]
    if sfi.journaled() {
        return sfi::activate(dirs, profile_id, install, &sfi);
    }
    let settled = sfi::settle(dirs, install)?;
    // Kiln entries an older Kiln recorded with their cook outputs in the hash move to their identity first, with the
    // profiles that list them: this activation then writes the game's profile file with the new entry
    crate::library::migrate_identities(dirs)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    let prev = read_game_profile(&install.path).ok().flatten().map(|f| f.generation).unwrap_or(0);
    let generation = next_generation(prev)?;
    let (mut file, mode, copied, copy_warnings) = prepare(dirs, &profile, install, &sfi.route, false)?;
    file.generation = generation; // grows with every write of the file
    let now = now_rfc3339();
    file.written = Some(now.clone());
    let pj = install.path.join(crate::paths::game::PROFILE);
    // Refuse the known staging conflict before changing any game-folder packages.
    let legacy_stage = crate::util::with_suffix(&pj, ".tmp");
    if std::fs::symlink_metadata(&legacy_stage).is_ok_and(|m| m.is_dir()) {
        return Err(Error::new(ErrorCode::Io, format!("{}: legacy profile staging path is a directory", legacy_stage.display())));
    }
    let previous = crate::game_files::placed(&install.path)?;
    let chain_path = install.path.join(crate::paths::game::CHAIN);
    let old_chain = if chain_path.is_file() { Some(std::fs::read(&chain_path).map_err(io_at(&chain_path, "reading"))?) } else { None };
    let mut chain_written = false;
    let changes: Result<_> = (|| {
        // native mods that run from the game folder itself (game_files): before chain.ini, which names the Ultimate ASI
        // Loader placed for them
        let game_files = crate::game_files::place(dirs, &profile, &install.path)?;
        // chain.ini first: its [chain] lines (a compat rule may switch a chained loader off) decide which own loaders run
        let (_, order_notes) = write_chain_plan(dirs, &profile, install, &file)?;
        chain_written = true;
        // ShroudForge and SET read their mods from their own folder in the game; the other engines' own loaders too.
        let mut placed = crate::engine_mods::place_routed(dirs, &profile, &install.path, &sfi.route)?;
        // a version a roll back put into the profile: the settings it had before the update go back into its copy
        placed.kept_files.extend(crate::kept::restore_after_place(dirs, &profile, &install.path));
        // Greeneye and Flame mods run from the copies place() just made in <game>/greeneye/ and <game>/mods/
        // (engine_mods::game_path_of)
        file.mods = render_routed(dirs, &profile, install, mode, &sfi.route, false)?.mods;
        crate::durable::json(&pj, &file, &[])?;
        Ok((game_files, order_notes, placed))
    })();
    let (game_files, order_notes, placed) = match changes {
        Ok(done) => done,
        Err(mut error) => {
            // profile.json still names the old activation. Restore the packages that were actually placed,
            // including when the saved profile has since been edited or placement stopped halfway through.
            if let Err(e) = crate::game_files::place_mods(dirs, &previous, &install.path) {
                error.message.push_str(&format!("; could not restore the earlier game-folder files: {}", e.message));
            }
            if chain_written {
                let restored = match old_chain {
                    Some(bytes) => crate::durable::replace(&chain_path, &bytes),
                    None => remove_if_there(&chain_path),
                };
                if let Err(e) = restored { error.message.push_str(&format!("; could not restore chain.ini: {}", e.message)); }
            }
            return Err(error);
        }
    };
    crate::kept::crash_point(crate::kept::Step::GameProfileWritten);
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
        own_loaders: placed.own_loaders,
        kept_files: placed.kept_files,
        warnings: skipped_native_loaders(dirs, &profile).into_iter().chain(copy_warnings).chain(placed.warnings).chain(placed.own_notes)
            .chain(game_files.notes).chain(order_notes).chain(xhl_twins(dirs, &profile, &install.path))
            .chain(settled)
            .collect(),
        retained: placed.retained.into_iter().chain(game_files.retained).collect(),
        safety_notes: placed.safety_notes,
    };
    let _ = crate::durable::json(&activation_record(dirs, install), &a, &[pj.clone()]);
    sfi::after(dirs, install, &sfi, sfi::After::Activated);
    Ok(a)
}

/// Whether two game-side profile files say the same, apart from the file's write counter, time and writer (and the
/// launch plan a kiln-mode activation pinned, which [`refresh_active`] checks on its own).
fn same_file(a: &ProfileFile, b: &ProfileFile) -> bool {
    let norm = |f: &ProfileFile| ProfileFile {
        schema: None,
        generation: 0,
        written: None,
        writer: None,
        #[cfg(feature = "sfi-scan")]
        sf_launch_plan: None,
        ..f.clone()
    };
    norm(a) == norm(b)
}

/// Re-writes the game's `kiln/profile.json` when its profile changed since it was written (edited in the launcher, by
/// `kilnctl profile add`, in another window): the profile is rendered again and compared with the file, apart from the
/// file's write counter, time and writer. Some(activation) when it re-activated. A current file still has its fixed
/// folders reconciled (engine_mods::place): when that changes what the profile renders to (a copy placed only now, as
/// for a profile.json an older Kiln wrote), the profile is activated after all; when it kept copies, files or notes
/// (a placed copy the player changed since), Some(the recorded activation with that report), so Play shows it too.
/// None when there is nothing to report or the file names no profile of this data folder (an id this folder does not
/// have, or another data folder's profile: [`game_profile_owner`]). Refuses while the game runs (activate).
pub fn refresh_active(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Activation>> {
    let Some(file) = read_game_profile(&install.path)? else { return Ok(None) };
    let Ok(profile) = get(dirs, &file.id) else { return Ok(None) };
    // another data folder's profile with an id this folder has too: left alone ([`game_profile_owner`] says so)
    if !file_is_here(dirs, install, &file) {
        return Ok(None);
    }
    let mode = match file.path_mode {
        Some(m) => m,
        None => crate::game::path_mode(install, dirs)?,
    };
    // (the route decided once; kiln mode reconciles and keeps or renews its plan journaled)
    let sfi = sfi::Decision::of(install);
    #[cfg(feature = "sfi-scan")]
    if sfi.journaled() {
        return sfi::refresh(dirs, install, &sfi, file, profile, mode);
    }
    // a profile that no longer renders (a mod gone from the library) goes to activate, which says why
    if render_routed(dirs, &profile, install, mode, &sfi.route, false).is_ok_and(|fresh| same_file(&fresh, &file)) {
        // A play-once profile also changes SF/SET's fixed folders. Reconcile those copies before the next normal
        // launch even though profile.json itself still describes the active profile. Never change them mid-game.
        if running_process(install)?.is_none() {
            // (an unfinished kiln-mode activation is settled before a copy changes)
            let settled = sfi::settle(dirs, install)?;
            let game_files = crate::game_files::place(dirs, &profile, &install.path)?;
            let mut placed = crate::engine_mods::place_routed(dirs, &profile, &install.path, &sfi.route)?;
            placed.warnings.extend(settled);
            placed.kept_files.extend(crate::kept::restore_after_place(dirs, &profile, &install.path));
            placed.warnings.extend(game_files.notes);
            placed.retained.extend(game_files.retained);
            // a copy placed only now (an older Kiln's file names the Library entry): the file must name the copy
            if !render_routed(dirs, &profile, install, mode, &sfi.route, false).is_ok_and(|fresh| same_file(&fresh, &file)) {
                let mut a = activate(dirs, &profile.id, install)?;
                // Restored files and other one-time notes belong to this operation, even when the second placement
                // has nothing left to do. Retained copies remain the final activation's current-state report.
                a.keep_notes(placed.kept_files, placed.warnings.into_iter().chain(placed.own_notes).collect());
                for n in placed.safety_notes { if !a.safety_notes.contains(&n) { a.safety_notes.push(n); } }
                return Ok(Some(a));
            }
            // the profile's late natives and the compat rules live in chain.ini only; when the rules changed the load
            // order (Kiln's shipped rules after an update, a mod added to the Library), the start says so
            let (wrote, order_notes) = write_chain_plan(dirs, &profile, install, &file)?;
            let order_notes = if wrote { order_notes } else { Vec::new() };
            // A player's own folder that still runs although its mod is off is said at every reconcile too (the plain
            // own-folder notes are not repeated; the launcher raises each sentence once until it is closed).
            if !(placed.retained.is_empty() && placed.kept_files.is_empty() && placed.warnings.is_empty() && placed.safety_notes.is_empty()
                && order_notes.is_empty())
                && let Some(mut a) = active(dirs, install)?
            {
                a.own_loaders = placed.own_loaders;
                a.kept_files = placed.kept_files;
                a.warnings = placed.warnings.into_iter().chain(placed.safety_notes.iter().cloned()).chain(order_notes).collect();
                a.safety_notes = placed.safety_notes;
                a.retained = placed.retained;
                return Ok(Some(a));
            }
        }
        return Ok(None);
    }
    activate(dirs, &profile.id, install).map(Some)
}

/// Undoes what [`render`] does to a profile's list for an enabled EML package that needs ShroudForge's runtime
/// (`eml_runtime`): kiln-eml's route switched off, and the compatibility entry (an exact copy of the EML entry, switched
/// on) in mods.shroudforge. A ShroudForge entry that differs in any field is a mod of the profile's and stays.
fn without_runtime_eml_signals(mut file: ProfileFile) -> ProfileFile {
    for id in &file.eml_runtime {
        let Some(m) = file.mods.eml.iter_mut().find(|m| &m.id == id) else { continue };
        m.enabled = true;
        let compat = m.clone();
        if let Some(i) = file.mods.shroudforge.iter().position(|s| *s == compat) {
            file.mods.shroudforge.remove(i);
        }
    }
    file
}

/// Launch mode once: renders the profile into `kiln/profile.once.json` and its chain plan (dllmain natives, the [chain]
/// and [late] lists with its compat rules applied) into `kiln/chain/once.ini` (play-once mode=profile then boots it for
/// one start).
pub fn write_once(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<ProfileFile> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    // (the route decided once for this start; an unfinished kiln-mode activation settled first)
    let sfi = sfi::Decision::of(install);
    sfi::settle(dirs, install)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    // These engines consume fixed folders, not the profile's paths. Changing them for a single start would also
    // change the next Steam launch, which does not call refresh_active. Only a persistent activation may do that.
    // The active file as its profile listed it: render's own entries for EML packages that need ShroudForge's runtime
    // (the compatibility entry in mods.shroudforge, kiln-eml's route switched off) are not a selection of the player's.
    let active = read_game_profile(&install.path)?.map(without_runtime_eml_signals);
    // EML, EMF, Greeneye, Flame and Shroudtopia read a fixed folder too while their own loader is installed (Kiln
    // copies the active profile's mods there): their selection cannot change for a single start either.
    // The view knows the profile's EML packages that need ShroudForge's runtime (the Library says so), as render's does:
    // Kiln hosts ShroudForge for them alone, and ShroudForge then runs the EML copies placed in its folder.
    let view = crate::own_loaders::ProfileView::of_profile(&profile).with_library(dirs, &profile);
    let loaders = crate::own_loaders::find_routed(&install.path, Some(&view), &sfi.route);
    // Name the runtime conflict before a fixed-folder refusal suggests activating this unsafe combination.
    crate::own_loaders::check_runtime_eml_loader(&loaders, &view.eml_runtime)?;
    let fixed = crate::own_loaders::PLACED_FOR_LOADER.into_iter().filter(|k| crate::own_loaders::of_kind(&loaders, *k)
        .is_some_and(|l| l.delivery == crate::own_loaders::Delivery::Placed));
    // Greeneye, Flame and dllmod packages run from Kiln's copies in <game>/greeneye/, <game>/mods/ and
    // <game>/Mods/EnshroudedModLoader/ (engine_mods::run_folder): a fixed folder as well
    for kind in [ModKind::ShroudForge, ModKind::Set, ModKind::Greeneye, ModKind::Flame, ModKind::Dllmod].into_iter()
        .chain(fixed.filter(|k| crate::engine_mods::run_folder(*k).is_none())) {
        let mut wanted: Vec<_> = profile.mods.list(kind).iter()
            .map(|m| (m.id.as_str(), Some(m.version.as_str()), Some(m.hash.as_str()), m.enabled)).collect();
        let mut current: Vec<_> = active.as_ref().and_then(|f| f.mods.folders(kind)).into_iter().flatten()
            .map(|m| (m.id.as_str(), m.version.as_deref(), m.hash.as_deref(), m.enabled)).collect();
        wanted.sort_unstable();
        current.sort_unstable();
        if wanted != current {
            let why = match crate::own_loaders::of_kind(&loaders, kind) {
                Some(l) if crate::own_loaders::PLACED_FOR_LOADER.contains(&kind) => format!(
                    "{} reads its mods from {}/ itself, so {} mods use a fixed game folder", crate::own_loaders::loader_name(l),
                    l.folder, crate::own_loaders::short(kind)),
                _ => format!("{} mods use a fixed game folder", kind.label()),
            };
            return Err(Error::new(ErrorCode::Unsupported,
                format!("{why}; activate this profile to change them before playing (play-once keeps the active selection)")));
        }
    }
    // EML/native companions a DLL reads in the game's mods folder are fixed copies there too, and so are EMF's Lua mods
    // in emf/mods (engine_mods::EMF_MODS)
    let in_game = |kind, mods: Vec<ProfileMod>| {
        let mut v: Vec<_> = mods.into_iter().filter(|m| match kind {
            ModKind::Eml | ModKind::Xhl => crate::engine_mods::game_folder_of(dirs, kind, m).is_some(),
            ModKind::Native => crate::library::path_of(dirs, &lref(kind, m)).is_ok_and(|p| !crate::library::native_settings_dirs(&p).is_empty()),
            ModKind::Emf => crate::library::path_of(dirs, &lref(kind, m)).is_ok_and(|p| crate::library::is_emf_lua(&p)),
            _ => false,
        }).map(|m| (m.id, m.version, m.hash, m.enabled)).collect();
        v.sort_unstable();
        v
    };
    for kind in [ModKind::Eml, ModKind::Native, ModKind::Xhl, ModKind::Emf] {
        let current = active.as_ref().and_then(|f| f.mods.folders(kind)).map(|list| list.iter().map(|m| ProfileMod {
            id: m.id.clone(), version: m.version.clone().unwrap_or_default(), hash: m.hash.clone().unwrap_or_default(), enabled: m.enabled,
        }).collect()).unwrap_or_default();
        if in_game(kind, profile.mods.list(kind).clone()) != in_game(kind, current) {
            let what = if kind == ModKind::Emf {
                "EMF's Lua mods run from Kiln's copies in emf/mods, where EMF's lua mod reads them"
            } else {
                "Mods that a DLL reads in the game's mods folder (Auto Loot Plus, the XHL mods) are copies there"
            };
            return Err(Error::new(ErrorCode::Unsupported, format!("{what}; activate this profile to change them before playing (play-once \
                keeps the active selection)")));
        }
    }
    // Compare with verified placements, since the active profile may have unapplied edits.
    if crate::game_files::would_change(dirs, &profile.mods.native, &install.path)? {
        return Err(Error::new(ErrorCode::Unsupported, "Mods placed in the game folder itself (a plugin loader such as powrprof.dll, \
            Ultimate ASI Loader and .asi plugins) are fixed there; activate this profile to change them before playing (play-once \
            keeps the active selection)"));
    }
    // (kiln mode: the start's own launch plan, pinned by profile.once.json)
    #[cfg(feature = "sfi-scan")]
    if sfi.journaled() {
        return sfi::once(dirs, &profile, install, &sfi);
    }
    // XHL-Core named dinput8.dll runs from Kiln's copy under its own name; the active profile's copy stays as it is
    // (its receipts spelled by the route this start decided)
    crate::engine_mods::place_xhl_core_for_once_routed(dirs, &profile, &install.path, &sfi.route)?;
    let (mut file, _, _, _) = prepare(dirs, &profile, install, &sfi.route, false)?;
    file.written = Some(now_rfc3339());
    // The single start follows this profile's plan: its compat rules (a [chain] proxy switched off, entries loaded late)
    // and its late natives, not the active profile's that chain.ini holds. once.ini carries [profile], [chain] and [late].
    let entries = crate::compat::profile_entries(dirs, &profile, install, &file)?;
    crate::chain::write_once(&install.path, &crate::compat::plan(dirs, &profile, install, &entries).chain)?;
    crate::util::write_json_atomic(&install.path.join(crate::paths::game::PROFILE_ONCE), &file)?;
    sfi::after(dirs, install, &sfi, sfi::After::Once);
    Ok(file)
}

/// Back to the legacy layout: profile.json and chain.ini [profile] removed (a copy is kept in kiln/backups/).
pub fn deactivate(dirs: &DataDirs, install: &GameInstall) -> Result<()> {
    deactivate_report(dirs, install).map(|_| ())
}

/// Deactivate with the fixed-folder cleanup result. Player-edited copies are retained, not silently deleted.
pub fn deactivate_report(dirs: &DataDirs, install: &GameInstall) -> Result<crate::engine_mods::PlacementCleanup> {
    refuse_running(install)?;
    // (an unfinished kiln-mode activation is settled before a copy is removed)
    let sfi = sfi::Decision::of(install);
    sfi::settle(dirs, install)?;
    let mut cleanup = crate::engine_mods::unplace(dirs, &install.path)?;
    let files = crate::game_files::unplace(&install.path)?;
    cleanup.removed.extend(files.removed);
    cleanup.retained.extend(files.retained);
    cleanup.kept_files.extend(files.kept_files);
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
    // (no profile pins a plan now)
    sfi::after(dirs, install, &sfi, sfi::After::Deactivated);
    Ok(cleanup)
}

/// The own loaders installed now and what they mean for the active profile file (checked again on every call: the
/// player may have added or removed a loader since the activation).
fn own_loader_notes(install: &GameInstall, file: &ProfileFile) -> Vec<crate::own_loaders::OwnLoaderNote> {
    let view = crate::own_loaders::ProfileView::of_file(file).with_file_sources(file, &install.path);
    crate::own_loaders::notes(&install.path, &crate::own_loaders::find(&install.path, Some(&view)), Some(&view))
}

pub fn active(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Activation>> {
    let Some(file) = read_game_profile(&install.path)? else { return Ok(None) };
    if let Ok(Some(mut a)) = crate::util::read_json::<Activation>(&activation_record(dirs, install))
        && a.profile_id == file.id
        && a.generation == file.generation
    {
        a.own_loaders = own_loader_notes(install, &file);
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
        own_loaders: own_loader_notes(install, &file),
        kept_files: Vec::new(),
        warnings: Vec::new(),
        retained: Vec::new(),
        safety_notes: Vec::new(),
    }))
}

/// kms1 over the profile's enabled entries (hash::fingerprint).
pub fn fingerprint(profile: &Profile) -> String {
    let entries = ModKind::ALL.iter().flat_map(|k| {
        let list = profile.mods.list(*k);
        list.iter().filter(|m| m.enabled).map(move |m| crate::hash::FingerprintEntry {
            kind: k.as_str().into(),
            id: m.id.clone(),
            version: m.version.clone(),
            hash: m.hash.clone(),
        })
    });
    crate::hash::fingerprint(entries)
}

/// Tests: what `save` runs between reading the stored profile and writing it (the window a delete could fall into).
#[cfg(test)]
static SAVE_PAUSE: std::sync::Mutex<Option<std::sync::Arc<dyn Fn(&Path) + Send + Sync>>> = std::sync::Mutex::new(None);

#[cfg(test)]
fn save_pause(root: &Path) {
    let f = SAVE_PAUSE.lock().unwrap().clone();
    if let Some(f) = f { f(root); }
}

#[cfg(test)]
mod review_tests {
    use super::*;

    /// An installed pack never takes a name a profile has: "My mods (2)", "(3)"..., any case, at most 128 characters.
    #[test]
    fn unique_names_add_a_number_when_a_profile_has_the_name() {
        let taken: std::collections::BTreeSet<String> = ["my mods", "my mods (2)", "ember run (emberfox's pack)"].iter().map(|s| s.to_string()).collect();
        assert_eq!(unique_among("My mods", &taken), "My mods (3)");
        assert_eq!(unique_among("  MY MODS ", &taken), "MY MODS (3)");
        assert_eq!(unique_among("Ember run (Emberfox's pack)", &taken), "Ember run (Emberfox's pack) (2)");
        assert_eq!(unique_among("Cinder Trails", &taken), "Cinder Trails");
        let long = "x".repeat(130);
        let taken: std::collections::BTreeSet<String> = [long[..128].to_string()].into_iter().collect();
        let n = unique_among(&long, &taken);
        assert_eq!(n.chars().count(), 128);
        assert!(n.ends_with(" (2)"), "{n}");
    }

    /// More than three changed copies of one mod set aside within one second: the pruning frees the oldest names, and
    /// the next copy never takes one of them back (it would sort as the oldest and be deleted at once).
    #[test]
    fn copies_set_aside_in_one_second_keep_the_newest_three() {
        let root = std::env::temp_dir().join(format!("km-profile-copy-names-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        let prefix = "profile-copy-my-mods-shroudforge-tweaks-";
        let base = root.join(format!("{prefix}20261118T234617Z"));
        // another mod whose id starts with this one's is not this mod's copy
        std::fs::create_dir_all(root.join("profile-copy-my-mods-shroudforge-tweaks-live-20261118T234617Z-9")).unwrap();
        let mut made = Vec::new();
        for i in 1..=6 {
            let p = next_copy_name(&base);
            std::fs::create_dir_all(&p).unwrap();
            std::fs::write(p.join("n"), i.to_string()).unwrap();
            made.push(p.file_name().unwrap().to_string_lossy().into_owned());
            prune_profile_copies(&root, prefix);
        }
        assert_eq!(made, [
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z",
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z-2",
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z-3",
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z-4",
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z-5",
            "profile-copy-my-mods-shroudforge-tweaks-20261118T234617Z-6",
        ]);
        let mut kept: Vec<String> = std::fs::read_dir(&root).unwrap().flatten()
            .filter(|e| e.file_name().to_string_lossy().starts_with(&format!("{prefix}2")))
            .map(|e| std::fs::read_to_string(e.path().join("n")).unwrap()).collect();
        kept.sort();
        assert_eq!(kept, ["4", "5", "6"]);
        assert!(root.join("profile-copy-my-mods-shroudforge-tweaks-live-20261118T234617Z-9").is_dir());
        let _ = std::fs::remove_dir_all(&root);
    }

    /// A save that read the profile before another worker deleted it and made it again (same id, same generation)
    /// never writes after that: the delete and the create wait for the save, so the save lands on the old profile,
    /// which is then deleted, and the new one is untouched.
    #[test]
    fn a_save_that_began_before_a_delete_never_writes_over_the_profile_made_again() {
        let root = std::env::temp_dir().join(format!("km-profile-race-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let dirs = DataDirs::at(root.join("data"));
        let mut old = create(&dirs, "My mods", None).unwrap();
        old.notes = "written by the save that began first".into();
        let (go, went) = std::sync::mpsc::channel::<()>();
        let worker = {
            let dirs = dirs.clone();
            std::thread::spawn(move || {
                went.recv().unwrap();
                delete(&dirs, "my-mods").unwrap();
                std::thread::sleep(std::time::Duration::from_millis(3));
                create(&dirs, "My mods", None).unwrap()
            })
        };
        let ours = dirs.root.clone();
        *SAVE_PAUSE.lock().unwrap() = Some(std::sync::Arc::new(move |root: &Path| {
            if root != ours { return; }
            let _ = go.send(());
            // the other worker gets every chance to delete and make the profile again inside this window
            std::thread::sleep(std::time::Duration::from_millis(150));
        }));
        let saved = save(&dirs, &old);
        *SAVE_PAUSE.lock().unwrap() = None;
        let made = worker.join().unwrap();
        assert!(saved.is_ok(), "the save came first and wrote the profile it read: {saved:?}");
        let now = get(&dirs, "my-mods").unwrap();
        assert_eq!(now.created, made.created);
        assert_eq!(now.notes, "", "the profile made again is untouched by the earlier save");
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn shared_engine_files_are_guarded_against_both_game_executables() {
        let dir = Path::new("/fixture/game");
        for (selected, running) in [(crate::game::CLIENT_EXE, crate::game::SERVER_EXE),
                                    (crate::game::SERVER_EXE, crate::game::CLIENT_EXE)] {
            let found = running_process_with(dir, selected, |queried_dir, exe| {
                assert_eq!(queried_dir, dir);
                Ok((exe == running).then(|| crate::proc::ProcessInfo { pid: 42, name: running.into(), exe: None, cmdline: vec![] }))
            }).unwrap().expect("the other executable must also prevent writes to shared engine folders");
            assert_eq!(found.name, running);
        }
        assert!(running_process_with(dir, crate::game::CLIENT_EXE, |_, _| Ok(None)).unwrap().is_none());
        assert!(running_process_with(dir, crate::game::CLIENT_EXE, |_, _| Err(Error::invalid("cannot inspect processes"))).is_err());
    }
}
