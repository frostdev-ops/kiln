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
/// (see [`Dropins`]); Ember's mods do not run, so it has no switch.
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
        })
    }
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
}

impl Activation {
    /// Two activations in one operation (a profile made or resumed, then written again with the added mods): what the
    /// earlier one kept or noted is kept in this one's lists. `retained` stays this one's: it is the game folder now.
    pub fn keep_notes_of(&mut self, earlier: Activation) {
        for (mine, theirs) in [(&mut self.kept_files, earlier.kept_files), (&mut self.warnings, earlier.warnings)] {
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
    pub enabled: bool,
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
        dropins_engines: src.as_ref().is_some_and(|s| s.dropins_engines),
        data_dir: src.as_ref().map(|s| s.data_dir).unwrap_or_default(),
        server: src.as_ref().and_then(|s| s.server.clone()),
        late: src.as_ref().map(|s| s.late.clone()).unwrap_or_default(),
    };
    check_profile(&p)?;
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
    p.generation = next_generation(p.generation)?;
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
        kiln_build: None,
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

pub fn resolve_preview(dirs: &DataDirs, profile: &Profile, side: RunSide) -> Result<ResolvePreview> {
    resolve_preview_for(dirs, profile, side, true)
}

/// [`resolve_preview`] for a game whose kiln.json says whether a client's server mods start with the player's own
/// world (`hosted`: [`hosted_server_mods`]).
pub fn resolve_preview_for(dirs: &DataDirs, profile: &Profile, side: RunSide, hosted: bool) -> Result<ResolvePreview> {
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
        let server_dirs = candidates.iter().filter(|c| c.manifest.as_ref().is_some_and(|m| m.side == crate::manifest::Side::Server))
            .map(|c| c.dir.clone()).collect();
        let world = if hosted { Some(crate::resolver::resolve(&candidates, &Environment { hosting: true, ..environment(side) })?) } else { None };
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
    for k in ModKind::ALL.into_iter().filter(|k| *k == ModKind::Plugin || k.is_engine()) {
        for m in list_of(&profile.mods, k) {
            let p = crate::library::path_of(dirs, &lref(k, m)).ok();
            if !p.as_ref().is_some_and(|p| p.exists()) {
                warnings.push(format!("{} {} {} is not in the library", k.as_str(), m.id, m.version));
            }
        }
    }
    Ok(ResolvePreview { side, kiln, native: ordered, warnings, with_world: starts_with_world })
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

/// The reader revision (native/core/src/profile.h KILN_PROFILE_READER) from which a core reads Copy mode's paths into
/// a game folder outside kiln/: `../mods/<folder>` in mods.eml, `../greeneye/<id>` in mods.greeneye.
pub const PROFILE_READER_GAME_MODS: u32 = 2;

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
    check_profile(profile)?;
    let pid = &profile.id;
    let mut mods = ProfileFileMods::default();
    // Copy mode names a game folder outside kiln/ (../mods/<folder>, ../greeneye/<id>) only for a core that reads that
    // form, and only under its folder name rule: every older core calls the whole profile invalid and loads no mods,
    // so those entries keep the copy under kiln/profiles/ (sync_copies makes it), as before the form existed.
    let reader = std::cell::OnceCell::new();
    let outside_kiln_ok = |placed: &Path| *reader.get_or_init(|| runtime_profile_reader(dirs, install)) >= PROFILE_READER_GAME_MODS
        && placed.file_name().and_then(|n| n.to_str()).is_some_and(crate::engine_mods::is_game_folder_name);
    for k in ModKind::ALL.into_iter().filter(|k| *k != ModKind::Plugin) {
        for m in list_of(&profile.mods, k) {
            let folder = crate::library::path_of(dirs, &lref(k, m))?;
            // ShroudForge and SET read their mods from their own folder in the game: the copy placed there
            // (their plugins take the list as "this profile uses the engine"; copy mode keeps the relative copy path)
            // An EML mod a native DLL reads in the game's mods folder runs from the copy placed there, so the DLL and
            // the Lua read the same settings (engine_mods::game_folder_of)
            let game_path = crate::engine_mods::game_path_of(dirs, k, m, &install.path);
            let placed = game_path.clone().or_else(|| crate::engine_mods::placed_folder(k, &install.path, &m.id));
            let path = match placed {
                Some(placed) if mode != PathMode::Copy => game_or_copy(&placed, install, mode, String::new)?,
                Some(placed) if game_path.is_some() && outside_kiln_ok(&placed) => format!("../{}", placed.strip_prefix(&install.path)
                    .map_err(|_| Error::invalid("EML game folder is outside the install"))?.to_string_lossy().replace('\\', "/")),
                _ => game_or_copy(&folder, install, mode, || copy_rel(pid, k, &m.id))?,
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

/// Copy mode: `<game>/kiln/profiles/<pid>/<kind>/<id>/` hold verified copies of the enabled mods; unchanged copies
/// are kept, copies of mods the profile no longer lists are removed. Returns the number of folders copied.
fn sync_copies(dirs: &DataDirs, profile: &Profile, install: &GameInstall) -> Result<u32> {
    let base = install.path.join(crate::paths::game::COPY_PROFILES).join(&profile.id);
    let mut copied = 0;
    // A Greeneye mod whose current copy is in <game>/greeneye/ runs from there when the core reads `../greeneye/<id>`
    // (render): no copy here. An older core gets this copy, as the profile then names it.
    let reader = std::cell::OnceCell::new();
    let runs_placed = |k: ModKind, m: &ProfileMod| k == ModKind::Greeneye
        && crate::engine_mods::game_path_of(dirs, k, m, &install.path).is_some()
        && *reader.get_or_init(|| runtime_profile_reader(dirs, install)) >= PROFILE_READER_GAME_MODS;
    for k in ModKind::ALL {
        let kdir = base.join(k.as_str());
        let wanted: Vec<&ProfileMod> = list_of(&profile.mods, k).iter().filter(|m| m.enabled && !runs_placed(k, m)).collect();
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

/// chain.ini as the profile wants it: [profile], its late natives and the compat rules (compat::plan). Returns true
/// when it wrote.
fn write_chain_plan(dirs: &DataDirs, profile: &Profile, install: &GameInstall, file: &ProfileFile) -> Result<bool> {
    let entries = crate::compat::dllmain_entries(dirs, profile, file)?;
    let plan = crate::compat::plan(dirs, profile, install, &entries);
    crate::chain::write_profile_plan(&install.path, &plan.chain)
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

fn next_generation(generation: u64) -> Result<u64> {
    generation.checked_add(1).filter(|g| *g <= i64::MAX as u64)
        .ok_or_else(|| Error::new(ErrorCode::Schema, "profile generation is exhausted"))
}

/// The activation flow (contracts.md): refuse while running, path mode, copy, chain.ini [profile], profile.json.
pub fn activate(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<Activation> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    let prev = read_game_profile(&install.path).ok().flatten().map(|f| f.generation).unwrap_or(0);
    let generation = next_generation(prev)?;
    let (mut file, mode, copied) = prepare(dirs, &profile, install)?;
    file.generation = generation; // grows with every write of the file
    let now = now_rfc3339();
    file.written = Some(now.clone());
    // chain.ini first: its [chain] lines (a compat rule may switch a chained loader off) decide which own loaders run
    write_chain_plan(dirs, &profile, install, &file)?;
    // ShroudForge and SET read their mods from their own folder in the game; the other engines' own loaders too.
    let placed = crate::engine_mods::place(dirs, &profile, &install.path)?;
    // Greeneye mods run from the copies place() just made in <game>/greeneye/ (engine_mods::game_path_of)
    file.mods = render(dirs, &profile, install, mode)?.mods;
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
        own_loaders: placed.own_loaders,
        kept_files: placed.kept_files,
        warnings: skipped_native_loaders(dirs, &profile).into_iter().chain(placed.warnings).chain(placed.own_notes).collect(),
        retained: placed.retained,
    };
    let _ = crate::util::write_json_atomic(&activation_record(dirs, install), &a);
    Ok(a)
}

/// Re-writes the game's `kiln/profile.json` when its profile changed since it was written (edited in the launcher, by
/// `kilnctl profile add`, in another window): the profile is rendered again and compared with the file, apart from the
/// file's write counter, time and writer. Some(activation) when it re-activated. A current file still has its fixed
/// folders reconciled (engine_mods::place): when that changes what the profile renders to (a copy placed only now, as
/// for a profile.json an older Kiln wrote), the profile is activated after all; when it kept copies, files or notes
/// (a placed copy the player changed since), Some(the recorded activation with that report), so Play shows it too.
/// None when there is nothing to report or the file names no profile of this data folder. Refuses while the game runs
/// (activate).
pub fn refresh_active(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Activation>> {
    let Some(file) = read_game_profile(&install.path)? else { return Ok(None) };
    let Ok(profile) = get(dirs, &file.id) else { return Ok(None) };
    let mode = match file.path_mode {
        Some(m) => m,
        None => crate::game::path_mode(install, dirs)?,
    };
    let same = |a: &ProfileFile, b: &ProfileFile| {
        let norm = |f: &ProfileFile| ProfileFile { schema: None, generation: 0, written: None, writer: None, ..f.clone() };
        norm(a) == norm(b)
    };
    // a profile that no longer renders (a mod gone from the library) goes to activate, which says why
    if render(dirs, &profile, install, mode).is_ok_and(|fresh| same(&fresh, &file)) {
        // A play-once profile also changes SF/SET's fixed folders. Reconcile those copies before the next normal
        // launch even though profile.json itself still describes the active profile. Never change them mid-game.
        if running_process(install)?.is_none() {
            let placed = crate::engine_mods::place(dirs, &profile, &install.path)?;
            // a copy placed only now (an older Kiln's file names the Library entry): the file must name the copy
            if !render(dirs, &profile, install, mode).is_ok_and(|fresh| same(&fresh, &file)) {
                return activate(dirs, &profile.id, install).map(Some);
            }
            // the profile's late natives and the compat rules live in chain.ini only
            write_chain_plan(dirs, &profile, install, &file)?;
            if !(placed.retained.is_empty() && placed.kept_files.is_empty() && placed.warnings.is_empty()) {
                if let Some(mut a) = active(dirs, install)? {
                    a.own_loaders = placed.own_loaders;
                    a.kept_files = placed.kept_files;
                    a.warnings = placed.warnings;
                    a.retained = placed.retained;
                    return Ok(Some(a));
                }
            }
        }
        return Ok(None);
    }
    activate(dirs, &profile.id, install).map(Some)
}

/// Launch mode once: renders the profile into `kiln/profile.once.json` and its chain plan (dllmain natives, the [chain]
/// and [late] lists with its compat rules applied) into `kiln/chain/once.ini` (play-once mode=profile then boots it for
/// one start).
pub fn write_once(dirs: &DataDirs, profile_id: &str, install: &GameInstall) -> Result<ProfileFile> {
    refuse_running(install)?;
    ensure_kiln(install)?;
    let profile = get(dirs, profile_id)?;
    check_profile(&profile)?;
    // These engines consume fixed folders, not the profile's paths. Changing them for a single start would also
    // change the next Steam launch, which does not call refresh_active. Only a persistent activation may do that.
    let active = read_game_profile(&install.path)?;
    // EML, EMF, Greeneye, Flame and Shroudtopia read a fixed folder too while their own loader is installed (Kiln
    // copies the active profile's mods there): their selection cannot change for a single start either.
    let loaders = crate::own_loaders::find(&install.path, Some(&crate::own_loaders::ProfileView::of_profile(&profile)));
    let fixed = crate::own_loaders::PLACED_FOR_LOADER.into_iter().filter(|k| crate::own_loaders::of_kind(&loaders, *k)
        .is_some_and(|l| l.delivery == crate::own_loaders::Delivery::Placed));
    // Greeneye mods run from Kiln's copies in <game>/greeneye/ (engine_mods::GREENEYE_MODS): a fixed folder as well
    for kind in [ModKind::ShroudForge, ModKind::Set, ModKind::Greeneye].into_iter().chain(fixed) {
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
    // EML/native companions a DLL reads in the game's mods folder are fixed copies there too.
    let in_game = |kind, mods: Vec<ProfileMod>| {
        let mut v: Vec<_> = mods.into_iter().filter(|m| match kind {
            ModKind::Eml | ModKind::Xhl => crate::engine_mods::game_folder_of(dirs, kind, m).is_some(),
            ModKind::Native => crate::library::path_of(dirs, &lref(kind, m)).is_ok_and(|p| !crate::library::native_settings_dirs(&p).is_empty()),
            _ => false,
        }).map(|m| (m.id, m.version, m.hash, m.enabled)).collect();
        v.sort_unstable();
        v
    };
    for kind in [ModKind::Eml, ModKind::Native, ModKind::Xhl] {
        let current = active.as_ref().and_then(|f| f.mods.folders(kind)).map(|list| list.iter().map(|m| ProfileMod {
            id: m.id.clone(), version: m.version.clone().unwrap_or_default(), hash: m.hash.clone().unwrap_or_default(), enabled: m.enabled,
        }).collect()).unwrap_or_default();
        if in_game(kind, profile.mods.list(kind).clone()) != in_game(kind, current) {
            return Err(Error::new(ErrorCode::Unsupported, "Mods that a DLL reads in the game's mods folder (Auto Loot Plus, the XHL mods) are \
                copies there; activate this profile to change them before playing (play-once keeps the active selection)"));
        }
    }
    let (mut file, _, _) = prepare(dirs, &profile, install)?;
    file.written = Some(now_rfc3339());
    // The single start follows this profile's plan: its compat rules (a [chain] proxy switched off, entries loaded late)
    // and its late natives, not the active profile's that chain.ini holds. once.ini carries [profile], [chain] and [late].
    let entries = crate::compat::dllmain_entries(dirs, &profile, &file)?;
    crate::chain::write_once(&install.path, &crate::compat::plan(dirs, &profile, install, &entries).chain)?;
    crate::util::write_json_atomic(&install.path.join(crate::paths::game::PROFILE_ONCE), &file)?;
    Ok(file)
}

/// Back to the legacy layout: profile.json and chain.ini [profile] removed (a copy is kept in kiln/backups/).
pub fn deactivate(dirs: &DataDirs, install: &GameInstall) -> Result<()> {
    deactivate_report(dirs, install).map(|_| ())
}

/// Deactivate with the fixed-folder cleanup result. Player-edited copies are retained, not silently deleted.
pub fn deactivate_report(dirs: &DataDirs, install: &GameInstall) -> Result<crate::engine_mods::PlacementCleanup> {
    refuse_running(install)?;
    let cleanup = crate::engine_mods::unplace(&install.path)?;
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
    Ok(cleanup)
}

/// The own loaders installed now and what they mean for the active profile file (checked again on every call: the
/// player may have added or removed a loader since the activation).
fn own_loader_notes(install: &GameInstall, file: &ProfileFile) -> Vec<crate::own_loaders::OwnLoaderNote> {
    let view = crate::own_loaders::ProfileView::of_file(file);
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

#[cfg(test)]
mod review_tests {
    use super::*;

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
