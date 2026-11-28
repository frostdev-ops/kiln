//! Configs of mods that are not installed (0.2.0-dev.31.30, HTR: Mods, More, Configs listed the configs of mods he had
//! removed): `config_orphans::list_profile` marks them, `mod_settings::clear_orphans` clears them after a copy into the
//! launcher's settings backups, and neither ever treats an installed mod's config, a mod that is only switched off, or
//! a file whose mod is not known as orphaned. Temp folders only.

use kiln_manage::config_orphans::{self, Installed};
use kiln_manage::game::{self, GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::mod_settings;
use kiln_manage::paths::DataDirs;
use kiln_manage::proc::ProcessInfo;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, Result};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-cfgorphans-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}
fn read(root: &Path, rel: &str) -> String {
    std::fs::read_to_string(root.join(rel)).unwrap_or_else(|e| panic!("{rel}: {e}"))
}

fn snap(file: &str, modid: &str) -> String {
    format!(r#"{{"format":1,"source":"kiln","file":"{file}","id":"{modid}","mod":"{modid}","entries":[]}}"#)
}
fn emf_snap(file: &str, modid: &str) -> String {
    format!(r#"{{"format":1,"source":"emf","file":"{file}","id":"{modid}","mod":"{modid}","valueFormat":"ini","entries":[]}}"#)
}

fn pm(id: &str, enabled: bool) -> ProfileMod {
    ProfileMod { id: id.into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "a".repeat(64)), enabled }
}

fn import(dirs: &DataDirs, root: &Path, id: &str) -> LibraryRef {
    let d = root.join(format!("src/{id}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }}"#).as_bytes());
    put(&d, "M.dll", id.as_bytes());
    let r = library::import(dirs, std::slice::from_ref(&d), &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.id == id).cloned().unwrap()
}

fn client(dirs: &DataDirs, path: &Path) -> GameInstall {
    std::fs::create_dir_all(path.join("kiln")).unwrap();
    let g = GameInstall {
        id: "g-client".into(), kind: GameKind::Client, path: path.to_path_buf(), exe: game::CLIENT_EXE.into(), app_id: kiln_manage::CLIENT_APP_ID,
        library: None, platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z,
        source: "manual".into(),
    };
    game::write_record(dirs, &g, None).unwrap();
    g
}

fn nothing(_: &Path, _: &str) -> Result<Option<ProcessInfo>> {
    Ok(None)
}

/// The profile "work" with one mod of each kind: `installed` is (kind, id, enabled).
fn profile_with(dirs: &DataDirs, installed: &[(ModKind, &str, bool)]) -> String {
    let mut p = profile::create(dirs, "work", None).unwrap();
    for (kind, id, on) in installed {
        match kind {
            ModKind::Kiln => p.mods.kiln.push(pm(id, *on)),
            ModKind::Native => p.mods.native.push(pm(id, *on)),
            ModKind::Emf => p.mods.emf.push(pm(id, *on)),
            ModKind::Eml => p.mods.eml.push(pm(id, *on)),
            ModKind::Xhl => p.mods.xhl.push(pm(id, *on)),
            ModKind::ShroudForge => p.mods.shroudforge.push(pm(id, *on)),
            ModKind::Set => p.mods.set.push(pm(id, *on)),
            other => panic!("{other:?}"),
        }
    }
    profile::save(dirs, &p).unwrap();
    p.id
}

/// A config folder with the config of one mod of every kind (a Kiln mod's file with its snapshot, a native mod's, EMF's
/// ini, and the engine kinds' `<kind>/<id>.json` without one), Kiln's own files and a file nobody can name.
fn seed(cfg: &Path) {
    for (file, who) in [("expeditions.json", "expeditions"), ("xhl-remote-chest.json", "xhl-remote-chest"), ("night-sky.json", "night-sky"),
        ("tool.json", "tool")] {
        put(cfg, file, b"{ \"on\": true }");
        put(cfg, &format!(".schema/{file}"), snap(file, who).as_bytes());
    }
    put(cfg, "emf/beastmaster.ini", b"[general]\nmax_pets=3\n");
    put(cfg, ".schema/emf/beastmaster.json", emf_snap("emf/beastmaster.ini", "beastmaster").as_bytes());
    put(cfg, "emf/mage.ini", b"[general]\n");
    put(cfg, ".schema/emf/mage.json", emf_snap("emf/mage.ini", "mage").as_bytes());
    for file in ["eml/max-stack.json", "xhl/chest-plus.json", "set/flymode.json", "shroudforge/gear_up.json"] {
        put(cfg, file, b"{ \"on\": true }");
    }
    put(cfg, "native.json", br#"{"format":1,"mods":{}}"#);
    put(cfg, "keybinds.json", b"{}");
    put(cfg, "rules.json", b"{}");
    put(cfg, ".schema/rules.json", snap("rules.json", "kiln").as_bytes());
    put(cfg, "mystery.json", b"{}");
    put(cfg, "shroudtopia/shroudtopia.json", b"{ \"mods\": { \"fly\": {} } }");
    // EMF's own: its core's settings, and the switch of the Lua host Kiln bundles (no `lua` mod is installed for it)
    put(cfg, "emf/emf_core.ini", b"[chat]\nadmins=76561198000000000\n");
    put(cfg, "emf/lua.ini", b"[lua]\nenabled=0\n");
}

fn orphaned(dirs: &DataDirs, pid: &str) -> Vec<String> {
    config_orphans::list_profile(dirs, pid).unwrap().into_iter().filter(|f| f.orphaned).map(|f| f.file).collect()
}

#[test]
fn a_config_of_a_mod_that_is_nowhere_is_orphaned_for_every_kind_of_mod() {
    let t = tmp("kinds");
    let dirs = DataDirs::at(t.0.join("data"));
    // nothing is installed: every file whose mod is known is orphaned, and only those
    let pid = profile_with(&dirs, &[]);
    seed(&dirs.profile(&pid).join("config"));
    assert_eq!(orphaned(&dirs, &pid), [
        "emf/beastmaster.ini", "emf/mage.ini", "eml/max-stack.json", "expeditions.json", "night-sky.json", "set/flymode.json",
        "shroudforge/gear_up.json", "tool.json", "xhl-remote-chest.json", "xhl/chest-plus.json",
    ]);
    // the list is the same one `config::list` gives, with the flag set
    let all = config_orphans::list_profile(&dirs, &pid).unwrap();
    assert_eq!(all.len(), kiln_manage::config::list(&dirs.profile(&pid).join("config")).unwrap().len());
    for f in all.iter().filter(|f| ["native.json", "keybinds.json", "rules.json", "mystery.json", "shroudtopia/shroudtopia.json", "emf/emf_core.ini",
        "emf/lua.ini"].contains(&f.file.as_str())) {
        assert!(!f.orphaned, "{} is nobody's config we know", f.file);
    }
}

#[test]
fn a_mod_that_is_installed_is_not_orphaned_on_or_off() {
    let t = tmp("installed");
    let dirs = DataDirs::at(t.0.join("data"));
    // one mod of each kind in the profile: the Kiln, native and engine ones on, the EMF, XHL and SET ones only switched off
    let pid = profile_with(&dirs, &[
        (ModKind::Kiln, "expeditions", true), (ModKind::Native, "tool", true), (ModKind::Emf, "beastmaster", false),
        (ModKind::Eml, "max-stack", true), (ModKind::Xhl, "chest-plus", false), (ModKind::Set, "flymode", false),
        (ModKind::ShroudForge, "gear_up", true),
    ]);
    seed(&dirs.profile(&pid).join("config"));
    assert_eq!(orphaned(&dirs, &pid), ["emf/mage.ini", "night-sky.json", "xhl-remote-chest.json"],
        "a switched off mod is installed; the others are the mods nothing lists");
}

#[test]
fn a_mod_in_the_library_or_a_game_folder_is_installed_too() {
    let t = tmp("elsewhere");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    let g = client(&dirs, &game);
    let pid = profile_with(&dirs, &[]);
    seed(&dirs.profile(&pid).join("config"));
    // in the Library (and in no profile): a row in the Mods list, off
    import(&dirs, &t.0, "expeditions");
    // a mod the player put into the game folder: Kiln did not place it, the Mods list shows it as found
    put(&g.path, "mods/night-sky/mod.json", br#"{"id":"night-sky","name":"Night Sky","version":"1.0.1"}"#);
    put(&g.path, "mods/night-sky/src/mod.lua", b"return {}");
    let orphans = orphaned(&dirs, &pid);
    assert!(!orphans.contains(&"expeditions.json".to_string()), "{orphans:?}");
    assert!(!orphans.contains(&"night-sky.json".to_string()), "{orphans:?}");
    assert!(orphans.contains(&"xhl-remote-chest.json".to_string()) && orphans.contains(&"emf/beastmaster.ini".to_string()), "{orphans:?}");
}

#[test]
fn nothing_is_marked_when_the_profile_cannot_be_read() {
    let t = tmp("unreadable");
    let dirs = DataDirs::at(t.0.join("data"));
    let pid = profile_with(&dirs, &[]);
    seed(&dirs.profile(&pid).join("config"));
    std::fs::write(dirs.profile(&pid).join("meta.json"), b"{ not json").unwrap();
    let installed = Installed::read(&dirs, &pid);
    assert!(installed.incomplete().is_some());
    assert!(orphaned(&dirs, &pid).is_empty(), "the launcher cannot tell, so it says nothing");
}

#[test]
fn clearing_moves_the_orphaned_files_to_the_settings_backups() {
    let t = tmp("clear");
    let dirs = DataDirs::at(t.0.join("data"));
    client(&dirs, &t.0.join("Enshrouded"));
    let pid = profile_with(&dirs, &[(ModKind::Kiln, "night-sky", false)]);
    let cfg = dirs.profile(&pid).join("config");
    seed(&cfg);
    let files = vec!["expeditions.json".to_string(), "emf/beastmaster.ini".to_string(), "xhl/chest-plus.json".to_string()];
    let r = mod_settings::clear_orphans_with(&dirs, &pid, &files, &mut nothing).unwrap();
    assert!(r.notes.is_empty(), "{:?}", r.notes);
    // the files, and the snapshots that went with them, are gone
    for gone in ["expeditions.json", ".schema/expeditions.json", "emf/beastmaster.ini", ".schema/emf/beastmaster.json", "xhl/chest-plus.json"] {
        assert!(!cfg.join(gone).exists(), "{gone} is still there");
    }
    assert_eq!(r.removed.len(), 5, "{:?}", r.removed);
    // everything else stays: the other orphans (not asked for), the installed mod's, Kiln's own, the unknown
    for stays in ["emf/mage.ini", "xhl-remote-chest.json", "night-sky.json", ".schema/night-sky.json", "native.json", "keybinds.json", "rules.json",
        "mystery.json", "eml/max-stack.json", "shroudtopia/shroudtopia.json"] {
        assert!(cfg.join(stays).is_file(), "{stays} was removed");
    }
    // the copy sits in the backups Remove uses (<data>/backups/mod-settings/), laid out as it was, with the record
    let backup = PathBuf::from(r.backup.clone().expect("a backup"));
    assert!(backup.starts_with(dirs.backups().join(mod_settings::BACKUP_DIR)), "{}", backup.display());
    assert_eq!(read(&backup, &format!("profiles/{pid}/config/expeditions.json")), "{ \"on\": true }");
    assert_eq!(read(&backup, &format!("profiles/{pid}/config/emf/beastmaster.ini")), "[general]\nmax_pets=3\n");
    assert!(read(&backup, &format!("profiles/{pid}/config/.schema/expeditions.json")).contains("expeditions"));
    let record: serde_json::Value = serde_json::from_str(&read(&backup, mod_settings::BACKUP_RECORD)).unwrap();
    assert_eq!(record["items"].as_array().unwrap().len(), 5);
    assert!(record["items"].as_array().unwrap().iter().any(|i| Path::new(i["from"].as_str().unwrap()).ends_with("expeditions.json")));
}

#[test]
fn clearing_refuses_an_installed_mods_config_and_changes_nothing() {
    let t = tmp("refuse");
    let dirs = DataDirs::at(t.0.join("data"));
    client(&dirs, &t.0.join("Enshrouded"));
    let pid = profile_with(&dirs, &[(ModKind::Kiln, "night-sky", true), (ModKind::Emf, "beastmaster", false)]);
    let cfg = dirs.profile(&pid).join("config");
    seed(&cfg);
    // an installed mod's file, one that is only switched off, Kiln's own, one nobody owns, one that is not there
    for file in ["night-sky.json", "emf/beastmaster.ini", "native.json", "rules.json", "mystery.json", "shroudtopia/shroudtopia.json", "gone.json",
        "emf/emf_core.ini", "emf/lua.ini"] {
        let e = mod_settings::clear_orphans_with(&dirs, &pid, &[file.to_string()], &mut nothing).unwrap_err();
        assert!(matches!(e.code, ErrorCode::Conflict | ErrorCode::NotFound), "{file}: {e:?}");
    }
    // one installed file among orphans: nothing at all is cleared
    let files = vec!["expeditions.json".to_string(), "night-sky.json".to_string()];
    let e = mod_settings::clear_orphans_with(&dirs, &pid, &files, &mut nothing).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict, "{e:?}");
    assert!(cfg.join("expeditions.json").is_file() && cfg.join("night-sky.json").is_file());
    assert!(!dirs.backups().join(mod_settings::BACKUP_DIR).exists(), "a refused clear leaves no backup folder");
    // names that leave the config folder
    for bad in ["../meta.json", "/etc/passwd", "a/../../x.json", ".schema/expeditions.json", ""] {
        assert!(mod_settings::clear_orphans_with(&dirs, &pid, &[bad.to_string()], &mut nothing).is_err(), "{bad}");
    }
    assert!(dirs.profile(&pid).join("meta.json").is_file());
}

#[test]
fn clearing_waits_for_the_game_and_for_a_mod_that_came_back() {
    let t = tmp("running");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    client(&dirs, &game);
    let pid = profile_with(&dirs, &[]);
    let cfg = dirs.profile(&pid).join("config");
    seed(&cfg);
    let files = vec!["expeditions.json".to_string()];
    let mut running = |d: &Path, exe: &str| -> Result<Option<ProcessInfo>> {
        Ok((d == game && exe == game::CLIENT_EXE).then(|| ProcessInfo { pid: 7, name: "enshrouded.exe".into(), exe: None, cmdline: vec![] }))
    };
    let e = mod_settings::clear_orphans_with(&dirs, &pid, &files, &mut running).unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning);
    assert!(cfg.join("expeditions.json").is_file());
    // the mod is added again after the window listed its file: it is installed now, so its file stays
    import(&dirs, &t.0, "expeditions");
    let e = mod_settings::clear_orphans_with(&dirs, &pid, &files, &mut nothing).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict, "{e:?}");
    assert!(cfg.join("expeditions.json").is_file());
    // no profile by that name
    assert_eq!(mod_settings::clear_orphans_with(&dirs, "nope", &files, &mut nothing).unwrap_err().code, ErrorCode::NotFound);
    // nothing asked, nothing done
    let r = mod_settings::clear_orphans_with(&dirs, &pid, &[], &mut nothing).unwrap();
    assert!(r.removed.is_empty() && r.backup.is_none());
}

#[test]
fn a_game_folder_is_read_only_while_a_files_mod_is_still_missing() {
    // review of 0.2.0-dev.31.31: config_list runs on every Mods page and every focus of the window; the profile and the
    // Library name the mods of most files, so the game folders (a scan of every mods folder of every known game) are
    // read only for the ids still missing. A place that cannot be read then matters only when an id is still missing
    let t = tmp("lazy");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    let g = client(&dirs, &game);
    let pid = profile_with(&dirs, &[(ModKind::Kiln, "expeditions", true), (ModKind::Emf, "beastmaster", false)]);
    import(&dirs, &t.0, "night-sky");
    put(&g.path, "greeneye/x/mod.json", b"{}");
    #[cfg(unix)]
    let unreadable = {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(g.path.join("greeneye"), std::fs::Permissions::from_mode(0o000)).unwrap();
        std::fs::read_dir(g.path.join("greeneye")).is_err() // root reads everything
    };
    #[cfg(not(unix))]
    let unreadable = false;
    let ids = |v: &[&str]| v.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    // in the profile (on or off) or in the Library: found without the game folder
    let some = Installed::read_for(&dirs, &pid, &ids(&["Expeditions", "beastmaster", "night-sky"]));
    assert_eq!(some.incomplete(), None, "{some:?}");
    assert!(some.has("expeditions") && some.has("night-sky"));
    assert_eq!(Installed::read_for(&dirs, &pid, &[]).incomplete(), None, "nothing asked: nothing to read");
    // one that is nowhere: the game folder is read, and one that cannot be read means Kiln cannot tell
    let gone = Installed::read_for(&dirs, &pid, &ids(&["expeditions", "gone"]));
    assert_eq!(gone.incomplete().is_some(), unreadable, "{gone:?}");
    assert!(!gone.has("gone"));
    // the whole set reads every place
    assert_eq!(Installed::read(&dirs, &pid).incomplete().is_some(), unreadable);
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(g.path.join("greeneye"), std::fs::Permissions::from_mode(0o755)).unwrap();
    }
}

#[test]
fn a_game_whose_profile_json_cannot_be_read_marks_nothing() {
    // review of 0.2.0-dev.31.31: the folders Kiln kept at the last activation come from the game's profile.json; one
    // that cannot be read was skipped, so a mod only in such a folder looked gone
    let t = tmp("badactive");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = client(&dirs, &t.0.join("Enshrouded"));
    let pid = profile_with(&dirs, &[]);
    seed(&dirs.profile(&pid).join("config"));
    put(&g.path, kiln_manage::paths::game::PROFILE, b"{ not json");
    let installed = Installed::read(&dirs, &pid);
    assert!(installed.incomplete().is_some_and(|w| w.contains("profile.json")), "{installed:?}");
    assert!(orphaned(&dirs, &pid).is_empty(), "the launcher cannot tell, so it says nothing");
    let e = mod_settings::clear_orphans_with(&dirs, &pid, &["expeditions.json".to_string()], &mut nothing).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict, "{e:?}");
    assert!(dirs.profile(&pid).join("config/expeditions.json").is_file());
}
