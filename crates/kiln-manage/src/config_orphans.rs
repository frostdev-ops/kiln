//! Configs of mods that are not installed (0.2.0-dev.31.31; HTR in #general: Mods, More, Configs listed the configs of
//! mods he had removed, and their switches looked live).
//!
//! Remove keeps a mod's settings on purpose (the settings files stay where Kiln keeps them, adding the mod again brings
//! them back), so a profile's config folder can hold files of mods that are gone. [`list_profile`] is `config::list`
//! with [`ConfigFileInfo::orphaned`] set on those, and `mod_settings::clear_orphans` is what clears them (after a copy
//! into the launcher's settings backups).
//!
//! "Not installed" means absent everywhere the launcher can see a mod ([`Installed`]): not in the profile (on or off:
//! a mod that is only switched off is installed), not in the Library (any kind, any version), not in the game folder of
//! a known game (a mod the player put there, or a folder Kiln kept at the last activation). It never guesses: a file
//! whose mod is not known for sure is not orphaned ([`owner_of`]), and when the launcher could not read one of the
//! places a mod can be in, no file is marked at all.

use crate::config::ConfigFileInfo;
use crate::manifest::ModKind;
use crate::paths::DataDirs;
use crate::Result;
use std::collections::HashSet;

/// The mods that are installed, by id (compared without case, as the Mods list's Configs entry does).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Installed {
    names: HashSet<String>,
    incomplete: Option<String>,
}

fn lower(s: &str) -> String {
    s.to_ascii_lowercase()
}

/// EMF's own files in a config folder's `emf/` (by stem): never a mod's ([`owner_of`]).
const EMF_OWN: [&str; 2] = ["emf_core", "lua"];

impl Installed {
    /// A set of ids (tests, and callers that know them).
    pub fn of<I, S>(ids: I) -> Installed
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        Installed { names: ids.into_iter().map(|s| lower(s.as_ref())).collect(), incomplete: None }
    }

    /// Every mod of the profile (on or off), the Library and the known game folders. Never fails: a place that could not
    /// be read is [`Installed::incomplete`], and then nothing is called orphaned (the places after it are not read, so
    /// [`Installed::has`] may miss a mod then).
    ///
    /// The game folders: what the Mods list shows as found (a mod the player put there: its id, its name and its file
    /// or folder name), and the folders Kiln kept at the last activation (a mod removed from the profile whose folder
    /// had changed files stays, and its loader may still run it).
    pub fn read(dirs: &DataDirs, profile_id: &str) -> Installed {
        Self::read_until(dirs, profile_id, None)
    }

    /// [`Installed::read`] for the mods of some files (`ids`, [`owner_of`]): the places are read in order (the profile,
    /// the Library, then each known game) only while one of `ids` is still missing (review of 0.2.0-dev.31.31). A game
    /// folder is a scan of all its mods folders, and `config_list` runs on every Mods page and every focus of the window,
    /// so a profile whose files' mods the profile or the Library names reads no game folder. What it says of `ids` is
    /// what [`Installed::read`] says.
    pub fn read_for(dirs: &DataDirs, profile_id: &str, ids: &[String]) -> Installed {
        Self::read_until(dirs, profile_id, Some(ids))
    }

    fn read_until(dirs: &DataDirs, profile_id: &str, ids: Option<&[String]>) -> Installed {
        let mut out = Installed::default();
        fn skip(out: &mut Installed, why: String) {
            out.incomplete.get_or_insert(why);
        }
        // nothing more to learn: a place could not be read (nothing is marked), or every id asked for is found
        let done = |out: &Installed| out.incomplete.is_some() || ids.is_some_and(|ids| ids.iter().all(|i| out.has(i)));
        let profile = match crate::profile::get(dirs, profile_id) {
            Ok(p) => Some(p),
            Err(e) => {
                skip(&mut out, format!("profile {profile_id} could not be read: {}", e.message));
                None
            }
        };
        if let Some(p) = &profile {
            for k in ModKind::ALL {
                out.names.extend(p.mods.list(k).iter().map(|m| lower(&m.id)));
            }
        }
        if done(&out) {
            return out;
        }
        match crate::library::list(dirs, None) {
            Ok(entries) => out.names.extend(entries.iter().filter(|e| !e.versions.is_empty()).map(|e| lower(&e.id))),
            Err(e) => skip(&mut out, format!("the Library could not be read: {}", e.message)),
        }
        if done(&out) {
            return out;
        }
        let games = match crate::game::known(dirs) {
            Ok(g) => g,
            Err(e) => {
                skip(&mut out, format!("the games could not be listed: {}", e.message));
                Vec::new()
            }
        };
        let stem = |path: &str| {
            let name = path.trim_end_matches('/').rsplit(['/', '\\']).next().unwrap_or(path);
            lower(name.rsplit_once('.').map_or(name, |(s, _)| s))
        };
        for g in &games {
            if done(&out) {
                break;
            }
            if let Some(p) = &profile {
                let rep = crate::found::scan(dirs, g, p);
                if rep.truncated || !rep.unreadable.is_empty() {
                    skip(&mut out, format!("the mods in {} could not all be listed", g.path.display()));
                }
                for m in &rep.mods {
                    out.names.extend(m.id.iter().map(|i| lower(i)));
                    out.names.insert(lower(&m.name));
                    out.names.insert(stem(&m.path));
                    out.names.extend(m.profile_mod.iter().map(|i| lower(i)));
                }
            }
            match crate::profile::active(dirs, g) {
                Ok(Some(a)) => {
                    for r in &a.retained {
                        let path = r.path.to_string_lossy();
                        out.names.insert(stem(&path));
                        out.names.insert(lower(path.trim_end_matches(['/', '\\']).rsplit(['/', '\\']).next().unwrap_or("")));
                    }
                }
                Ok(None) => {}
                // the folders Kiln kept there are named in it (review of 0.2.0-dev.31.31: it was skipped)
                Err(e) => skip(&mut out, format!("the profile.json of {} could not be read: {}", g.path.display(), e.message)),
            }
        }
        out
    }

    pub fn has(&self, id: &str) -> bool {
        self.names.contains(&lower(id))
    }

    /// Why the set may miss a mod (a place that could not be read); None: it is the whole set.
    pub fn incomplete(&self) -> Option<&str> {
        self.incomplete.as_deref()
    }
}

/// The id of the mod a config file belongs to, None when that is not known for sure (the file stays as it is then).
///
/// The way the Mods list maps a config to its mod: the schema snapshot's `mod` (Kiln mods, native mods, plugins and
/// kiln-emf write one), or, for EMF's `emf/<id>.ini`, the file's name. Added here for the engines' own values files
/// (`<kind>/<id>.<ext>`, the places `mod_settings` takes a removed mod's settings from): the kind's folder and the file's
/// name. Never: Kiln's own files (`native.json`, `keybinds.json`, ...; the core's configs name the mod "kiln"), Shroudtopia's
/// one shared file, EMF's own (`emf/emf_core.ini`, `emf/lua.ini` of the Lua host Kiln bundles), and anything else, which
/// may be a mod's or the player's.
pub fn owner_of(f: &ConfigFileInfo) -> Option<String> {
    if f.kind == "native" {
        return None;
    }
    let mut parts = f.file.split('/');
    let (first, second, third) = (parts.next()?, parts.next(), parts.next());
    let stem_of = |name: &str| name.rsplit_once('.').map(|(s, _)| s.to_string());
    if second.is_none() && stem_of(first).is_some_and(|s| crate::mod_settings::KILN_OWN.contains(&lower(&s).as_str())) {
        return None;
    }
    // EMF's own files in its folder (review of 0.2.0-dev.31.31), whatever a snapshot says: `emf_core.ini`, EMF's core
    // settings (read from here before EMF's own), and `lua.ini`, the switch and settings of the Lua host Kiln bundles
    // (kiln/plugins/emf-lua), which kiln-emf reads whether or not a `lua` mod is installed
    if first == "emf" && third.is_none() && second.and_then(stem_of).is_some_and(|s| EMF_OWN.contains(&lower(&s).as_str())) {
        return None;
    }
    let id = match &f.mod_id {
        Some(id) => id.clone(),
        None => {
            // no snapshot: `<kind>/<id>.<ext>` of an engine's kind, and nothing deeper
            let kind = ModKind::parse(first).filter(|k| !matches!(k, ModKind::Kiln | ModKind::Native | ModKind::Plugin))?;
            let id = stem_of(second?).filter(|_| third.is_none())?;
            if kind == ModKind::Shroudtopia && id.eq_ignore_ascii_case("shroudtopia") {
                return None;
            }
            id
        }
    };
    (!id.is_empty() && !id.eq_ignore_ascii_case("kiln")).then_some(id)
}

/// Sets `orphaned` on every file whose mod is not in `installed`. Nothing is set while `installed` is incomplete.
pub fn mark(list: &mut [ConfigFileInfo], installed: &Installed) {
    let sure = installed.incomplete().is_none();
    for f in list {
        f.orphaned = sure && owner_of(f).is_some_and(|id| !installed.has(&id));
    }
}

/// `config::list` of a profile's config folder, with the orphaned files marked.
pub fn list_profile(dirs: &DataDirs, profile_id: &str) -> Result<Vec<ConfigFileInfo>> {
    let cfg = dirs.profile(profile_id).join("config");
    if !cfg.is_dir() {
        return Ok(vec![]);
    }
    let mut list = crate::config::list(&cfg)?;
    let mut owners: Vec<String> = Vec::new();
    for id in list.iter().filter_map(owner_of) {
        if !owners.iter().any(|o| o.eq_ignore_ascii_case(&id)) {
            owners.push(id);
        }
    }
    if !owners.is_empty() {
        mark(&mut list, &Installed::read_for(dirs, profile_id, &owners));
    }
    Ok(list)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn info(file: &str, mod_id: Option<&str>, kind: &str) -> ConfigFileInfo {
        ConfigFileInfo { file: file.into(), mod_id: mod_id.map(str::to_owned), kind: kind.into(), has_schema: mod_id.is_some(),
            mod_version: None, modified: None, orphaned: false }
    }

    #[test]
    fn a_config_names_its_mod_by_snapshot_or_by_the_engine_folder() {
        // a snapshot says whose a file is (Kiln mods, native mods and plugins write them; kiln-emf writes them for EMF)
        assert_eq!(owner_of(&info("expeditions.json", Some("expeditions"), "kiln")).as_deref(), Some("expeditions"));
        assert_eq!(owner_of(&info("night-sky-moons.json", Some("night-sky"), "kiln")).as_deref(), Some("night-sky"));
        assert_eq!(owner_of(&info("emf/beastmaster.ini", Some("beastmaster"), "emf")).as_deref(), Some("beastmaster"));
        // an engine's own values file, `<kind>/<id>.<ext>`, with no snapshot: the kind's folder and the file's name
        for (file, id) in [("eml/max-stack.json", "max-stack"), ("xhl/remote-chest.json", "remote-chest"), ("set/flymode.json", "flymode"),
            ("shroudforge/gear_up.json", "gear_up"), ("emf/old-mage.ini", "old-mage"), ("greeneye/g.ini", "g"), ("flame/f.json", "f")] {
            assert_eq!(owner_of(&info(file, None, if file.starts_with("emf/") { "emf" } else { "kiln" })).as_deref(), Some(id), "{file}");
        }
    }

    #[test]
    fn what_is_not_known_for_sure_has_no_owner() {
        // no snapshot, no engine folder: a file of some mod, or the player's own
        assert_eq!(owner_of(&info("sky.json", None, "kiln")), None);
        assert_eq!(owner_of(&info("notes.ini", None, "other")), None);
        assert_eq!(owner_of(&info("mine/sky.json", None, "kiln")), None, "a folder that is no engine's");
        assert_eq!(owner_of(&info("eml/deep/sky.json", None, "kiln")), None, "deeper than <kind>/<id>.<ext>");
        // Shroudtopia's one shared file holds every mod's entry: it is nobody's
        assert_eq!(owner_of(&info("shroudtopia/shroudtopia.json", None, "kiln")), None);
        // Kiln's own files, whatever a snapshot says: the core's configs name the mod "kiln"
        assert_eq!(owner_of(&info("native.json", None, "native")), None);
        for own in ["keybinds", "rules", "network", "chat", "permissions", "menus-client", "config-sync", "tk-config", "kiln"] {
            assert_eq!(owner_of(&info(&format!("{own}.json"), Some(own), "kiln")), None, "{own}");
            assert_eq!(owner_of(&info(&format!("{own}.json"), Some("some-mod"), "kiln")), None, "{own} is Kiln's, a snapshot cannot give it away");
        }
        assert_eq!(owner_of(&info("sync.json", Some("kiln"), "kiln")), None, "the core's own config (Config.Register outside a mod)");
        assert_eq!(owner_of(&info("sync.json", Some("KILN"), "kiln")), None);
        // Shroudtopia's shared file in another case (NTFS keeps the case it was made with)
        assert_eq!(owner_of(&info("shroudtopia/Shroudtopia.json", None, "kiln")), None);
    }

    #[test]
    fn emfs_own_files_in_its_folder_are_nobodys() {
        // review of 0.2.0-dev.31.31: `config::list` names `emf/<x>.ini` after its file, so these two were "Not installed"
        // and could be cleared. emf_core.ini: EMF's core settings (chat admins, [net] mods_check; docs/kiln/emf.md), read
        // from the config folder before EMF's own. lua.ini: the switch and settings of the Lua host Kiln bundles
        // (kiln/plugins/emf-lua), read whether or not a `lua` mod is installed (native/plugins/emf/tests/run-bundled-lua.sh
        // bundle-off), with or without a snapshot
        for (file, mod_id) in [("emf/emf_core.ini", Some("emf_core")), ("emf/lua.ini", Some("lua")), ("emf/LUA.ini", Some("LUA")),
            ("emf/lua.ini", None), ("emf/emf_core.ini", None)] {
            assert_eq!(owner_of(&info(file, mod_id, "emf")), None, "{file} {mod_id:?}");
        }
        // an EMF mod's own file is still its mod's
        assert_eq!(owner_of(&info("emf/lua-smoke.ini", Some("lua-smoke"), "emf")).as_deref(), Some("lua-smoke"));
        assert_eq!(owner_of(&info("emf/beastmaster.ini", Some("beastmaster"), "emf")).as_deref(), Some("beastmaster"));
    }

    #[test]
    fn only_the_files_of_mods_that_are_not_installed_are_marked() {
        let mut list = vec![
            info("expeditions.json", Some("expeditions"), "kiln"),
            info("emf/beastmaster.ini", Some("beastmaster"), "emf"),
            info("xhl-remote-chest.json", Some("xhl-remote-chest"), "kiln"),
            info("big-stacks.json", Some("big-stacks"), "kiln"),
            info("sky.json", None, "kiln"),
            info("native.json", None, "native"),
            info("rules.json", Some("kiln"), "kiln"),
        ];
        mark(&mut list, &Installed::of(["Big-Stacks", "Night-Sky"]));
        let marked: Vec<&str> = list.iter().filter(|f| f.orphaned).map(|f| f.file.as_str()).collect();
        assert_eq!(marked, ["expeditions.json", "emf/beastmaster.ini", "xhl-remote-chest.json"]);
    }

    #[test]
    fn nothing_is_marked_while_a_place_could_not_be_read() {
        let mut list = vec![info("expeditions.json", Some("expeditions"), "kiln")];
        let mut installed = Installed::of(["other"]);
        installed.incomplete = Some("the game folder could not be read".into());
        mark(&mut list, &installed);
        assert!(!list[0].orphaned);
    }
}
