//! Copy mode's config sync (profile::copy_back): the game's config copy (`<game>/kiln/profiles/<pid>/config`) and the
//! data folder's (`<data>/profiles/<pid>/config`, which the launcher's Configs editor writes) are reconciled file by file
//! against the record of what Kiln last copied into the game. Before, the game's copy overwrote every file: a launcher
//! edit was lost at the next activation (a mod update re-renders the profile) and after the game closed.
//! The launcher's game-exit path (launcher watch.rs) is profile::copy_back itself, called here directly.

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::config;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-config-sync-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}
fn read(p: &Path) -> String {
    std::fs::read_to_string(p).unwrap_or_else(|e| panic!("{}: {e}", p.display()))
}
/// A modification time `secs` from now: the order of two writes without waiting for the clock.
fn age(p: &Path, secs: i64) {
    let now = SystemTime::now();
    let t = if secs >= 0 { now + Duration::from_secs(secs as u64) } else { now - Duration::from_secs((-secs) as u64) };
    std::fs::File::options().write(true).open(p).unwrap().set_modified(t).unwrap();
}

fn kiln_mod(root: &Path, id: &str, version: &str) -> PathBuf {
    let d = root.join(format!("src/{id}-{version}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "{version}", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }}"#).as_bytes());
    put(&d, "M.dll", format!("{id} {version}").as_bytes());
    d
}

fn import(dirs: &DataDirs, src: PathBuf) -> LibraryRef {
    let r = library::import(dirs, &[src], &mut NoProgress).unwrap();
    r.imported.into_iter().chain(r.existing).find(|x| x.kind == ModKind::Kiln).unwrap()
}

fn pm(r: &LibraryRef) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true }
}

/// A copy-mode game (no Wine prefix: Flatpak Steam, a prefix without Z:) with Kiln installed, and a profile with one Kiln
/// mod, activated.
struct Fx {
    _t: Tmp,
    root: PathBuf,
    dirs: DataDirs,
    g: GameInstall,
    pid: String,
}

impl Fx {
    fn new(tag: &str) -> Fx {
        let t = tmp(tag);
        let root = t.0.clone();
        let dirs = DataDirs::at(root.join("data"));
        let game = root.join("game");
        put(&game, "enshrouded.exe", b"MZ");
        std::fs::create_dir_all(game.join("kiln")).unwrap();
        let g = GameInstall {
            id: "g-test".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None, build_id: None,
            exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
        };
        let r = import(&dirs, kiln_mod(&root, "kiln-auto-loot", "0.1.4"));
        let mut p = profile::create(&dirs, "Loot", None).unwrap();
        p.mods.kiln.push(pm(&r));
        let p = profile::save(&dirs, &p).unwrap();
        let a = profile::activate(&dirs, &p.id, &g).unwrap();
        assert_eq!(a.path_mode, PathMode::Copy);
        Fx { _t: t, root, dirs, g, pid: p.id }
    }
    fn game_cfg(&self) -> PathBuf {
        self.g.path.join("kiln/profiles").join(&self.pid).join("config")
    }
    fn data_cfg(&self) -> PathBuf {
        self.dirs.profile(&self.pid).join("config")
    }
    fn record(&self) -> PathBuf {
        profile::config_sync_record(&self.dirs, &self.pid, &self.g.id)
    }
    /// What the runtime does in the game: writes a config file into the game's copy.
    fn game_writes(&self, rel: &str, text: &str) {
        put(&self.game_cfg(), rel, text.as_bytes());
    }
    /// The launcher's Configs editor (ConfigTarget::Profile): the data folder's copy.
    fn launcher_sets(&self, file: &str, key: &str, value: serde_json::Value) {
        config::set(&self.data_cfg(), file, key, &value).unwrap();
    }
    /// The game closed (launcher watch.rs: profile::copy_back for the profile that ran).
    fn game_exits(&self) -> profile::ConfigSync {
        profile::copy_back(&self.dirs, &self.pid, &self.g).unwrap()
    }
    /// A mod update: a new version in the Library, the profile switched to it, and the launcher applying the profile
    /// again where it is active (commands::profile::reapply_where_active: profile::refresh_active).
    fn mod_update(&self, version: &str) -> profile::Activation {
        let r = import(&self.dirs, kiln_mod(&self.root, "kiln-auto-loot", version));
        let mut p = profile::get(&self.dirs, &self.pid).unwrap();
        p.mods.kiln[0] = pm(&r);
        profile::save(&self.dirs, &p).unwrap();
        profile::refresh_active(&self.dirs, &self.g).unwrap().expect("the profile changed: activated again")
    }
    fn conflicts_beside(&self, dir: &Path, name: &str) -> Vec<PathBuf> {
        let mut v: Vec<PathBuf> = std::fs::read_dir(dir).unwrap().flatten().map(|e| e.path())
            .filter(|p| p.file_name().unwrap().to_string_lossy().starts_with(&format!("{name}.conflict-"))).collect();
        v.sort();
        v
    }
}

const DEFAULTS: &str = "{\n  \"stackSize\": 20\n}\n";

/// The player's report (Nexus, Kiln Auto Loot): the stack size set in the launcher "gets reset" whenever the mod updates.
#[test]
fn a_launcher_edit_survives_a_mod_update_and_the_game_closing() {
    let fx = Fx::new("mod-update");
    // the game ran and Kiln wrote the mod's config from its defaults; the launcher brought it back when the game closed
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (1, 0), "{back:?}");
    assert_eq!(read(&fx.data_cfg().join("kiln-auto-loot.json")), DEFAULTS);
    // the player sets the stack size in the launcher's Configs page
    fx.launcher_sets("kiln-auto-loot.json", "stackSize", serde_json::json!(999));
    let edited = read(&fx.data_cfg().join("kiln-auto-loot.json"));
    assert!(edited.contains("999"), "{edited}");
    // the game closes again without touching the file: the edit stays
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (0, 0), "{back:?}");
    assert_eq!(read(&fx.data_cfg().join("kiln-auto-loot.json")), edited);
    // the mod updates: the profile is activated again, and the game gets the edit (before: the game's defaults came back)
    let a = fx.mod_update("0.1.5");
    assert!(a.warnings.iter().all(|w| !w.contains("kiln-auto-loot.json")), "no conflict: {:?}", a.warnings);
    assert_eq!(read(&fx.data_cfg().join("kiln-auto-loot.json")), edited);
    assert_eq!(read(&fx.game_cfg().join("kiln-auto-loot.json")), edited, "the game's copy is the edited one now");
    // and the game closing after that brings back what the game changed, not the old defaults
    fx.game_writes("kiln-auto-loot.json", "{\n  \"stackSize\": 999,\n  \"newKey\": true\n}\n");
    assert_eq!(fx.game_exits().copied, 1);
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("newKey"));
}

#[test]
fn an_edit_made_before_the_game_closes_survives_the_close_and_the_next_activation() {
    let fx = Fx::new("edit-then-exit");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    fx.game_writes("other-mod.json", "{ \"a\": 1 }\n");
    fx.game_exits();
    // while the game runs (or after it closed, before the launcher saw it), the player edits one file
    fx.launcher_sets("kiln-auto-loot.json", "stackSize", serde_json::json!(500));
    // the game changes the other file only
    fx.game_writes("other-mod.json", "{ \"a\": 2 }\n");
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (1, 0), "{back:?}");
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("500"), "the launcher's edit stays");
    assert_eq!(read(&fx.data_cfg().join("other-mod.json")), "{ \"a\": 2 }\n", "the game's change comes back");
    // a second close (Steam's Play button starts the game without an activation) still keeps it
    assert_eq!(fx.game_exits(), profile::ConfigSync::default());
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("500"));
    // the next activation puts it into the game
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    assert!(read(&fx.game_cfg().join("kiln-auto-loot.json")).contains("500"));
    assert_eq!(read(&fx.game_cfg().join("other-mod.json")), "{ \"a\": 2 }\n");
}

#[test]
fn an_in_game_edit_still_comes_back() {
    let fx = Fx::new("in-game");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    let a = profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    assert!(a.warnings.is_empty(), "{:?}", a.warnings);
    assert_eq!(read(&fx.data_cfg().join("kiln-auto-loot.json")), DEFAULTS);
    // the player changes the setting in the game's own settings page
    fx.game_writes("kiln-auto-loot.json", "{\n  \"stackSize\": 64\n}\n");
    let a = profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    assert!(a.warnings.is_empty(), "{:?}", a.warnings);
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("64"));
    assert!(read(&fx.game_cfg().join("kiln-auto-loot.json")).contains("64"));
}

#[test]
fn a_file_both_sides_changed_keeps_the_newer_and_saves_the_other_beside_it() {
    let fx = Fx::new("both");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    fx.game_writes("sub/deep.json", "{ \"v\": 0 }\n");
    fx.game_exits();
    // the game's is newer: it is kept, the launcher's is saved as <name>.conflict-<time>
    fx.launcher_sets("kiln-auto-loot.json", "stackSize", serde_json::json!(111));
    age(&fx.data_cfg().join("kiln-auto-loot.json"), -60);
    let launchers = read(&fx.data_cfg().join("kiln-auto-loot.json"));
    fx.game_writes("kiln-auto-loot.json", "{\n  \"stackSize\": 222\n}\n");
    // the launcher's is newer: it is kept, the game's is saved beside it in the data folder
    put(&fx.data_cfg(), "sub/deep.json", b"{ \"v\": 1 }\n");
    fx.game_writes("sub/deep.json", "{ \"v\": 2 }\n");
    age(&fx.game_cfg().join("sub/deep.json"), -60);
    let a = profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    let said: Vec<&String> = a.warnings.iter().filter(|w| w.contains("conflict-")).collect();
    assert_eq!(said.len(), 2, "one warning line each, in the activation's warnings: {:?}", a.warnings);
    assert!(said.iter().any(|w| w.contains("kiln-auto-loot.json") && w.contains("Kept the game's copy")), "{said:?}");
    assert!(said.iter().any(|w| w.contains("sub/deep.json") && w.contains("Kept the launcher's copy")), "{said:?}");
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("222"));
    let aside = fx.conflicts_beside(&fx.data_cfg(), "kiln-auto-loot.json");
    assert_eq!(aside.len(), 1, "{aside:?}");
    assert_eq!(read(&aside[0]), launchers, "the launcher's edit is kept, not lost");
    assert_eq!(read(&fx.data_cfg().join("sub/deep.json")), "{ \"v\": 1 }\n");
    let aside = fx.conflicts_beside(&fx.data_cfg().join("sub"), "deep.json");
    assert_eq!(aside.len(), 1, "{aside:?}");
    assert_eq!(read(&aside[0]), "{ \"v\": 2 }\n");
    assert!(a.warnings.iter().any(|w| w.contains(&aside[0].display().to_string())), "the warning names where it is");
    // the game got the kept copies; nothing is a conflict any more
    assert!(read(&fx.game_cfg().join("kiln-auto-loot.json")).contains("222"));
    assert_eq!(read(&fx.game_cfg().join("sub/deep.json")), "{ \"v\": 1 }\n");
    assert_eq!(fx.game_exits(), profile::ConfigSync::default());
}

/// At a game close the game's copy is not written: a conflict the launcher's copy won is said once, not at every close.
#[test]
fn a_conflict_at_the_game_closing_is_said_once() {
    let fx = Fx::new("exit-conflict");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    fx.game_exits();
    fx.game_writes("kiln-auto-loot.json", "{\n  \"stackSize\": 30\n}\n");
    age(&fx.game_cfg().join("kiln-auto-loot.json"), -60);
    fx.launcher_sets("kiln-auto-loot.json", "stackSize", serde_json::json!(40));
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (0, 1), "{back:?}");
    assert!(read(&fx.data_cfg().join("kiln-auto-loot.json")).contains("40"));
    assert_eq!(fx.game_exits(), profile::ConfigSync::default(), "the game's copy is the recorded one now");
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    assert!(read(&fx.game_cfg().join("kiln-auto-loot.json")).contains("40"));
}

#[test]
fn created_and_deleted_files() {
    let fx = Fx::new("create-delete");
    fx.game_writes("kept.json", "{ \"k\": 1 }\n");
    fx.game_writes("reset.json", "{ \"r\": 1 }\n");
    fx.game_writes("reset-then-game.json", "{ \"rg\": 1 }\n");
    fx.game_writes("gone-in-game.json", "{ \"g\": 1 }\n");
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    // a file the game creates comes back; one the launcher creates goes in
    fx.game_writes("new-in-game.json", "{ \"n\": 1 }\n");
    put(&fx.data_cfg(), "new-in-launcher.json", b"{ \"l\": 1 }\n");
    // the launcher's "reset a whole file" deletes it (Kiln writes it again from the defaults): it stays deleted
    config::reset(&fx.data_cfg(), "reset.json", None).unwrap();
    // deleted in the launcher, but the game changed it since: the change comes back
    config::reset(&fx.data_cfg(), "reset-then-game.json", None).unwrap();
    fx.game_writes("reset-then-game.json", "{ \"rg\": 2 }\n");
    // a file missing from the game's copy is never deleted from the data folder
    std::fs::remove_file(fx.game_cfg().join("gone-in-game.json")).unwrap();
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (2, 0), "{back:?}");
    assert_eq!(read(&fx.data_cfg().join("new-in-game.json")), "{ \"n\": 1 }\n");
    assert_eq!(read(&fx.data_cfg().join("new-in-launcher.json")), "{ \"l\": 1 }\n");
    assert!(!fx.data_cfg().join("reset.json").exists(), "the reset stays");
    assert_eq!(read(&fx.data_cfg().join("reset-then-game.json")), "{ \"rg\": 2 }\n");
    assert_eq!(read(&fx.data_cfg().join("gone-in-game.json")), "{ \"g\": 1 }\n");
    assert_eq!(read(&fx.data_cfg().join("kept.json")), "{ \"k\": 1 }\n");
    // the activation makes the game's copy the data folder's
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    assert!(!fx.game_cfg().join("reset.json").exists());
    for f in ["new-in-game.json", "new-in-launcher.json", "reset-then-game.json", "gone-in-game.json", "kept.json"] {
        assert_eq!(read(&fx.game_cfg().join(f)), read(&fx.data_cfg().join(f)), "{f}");
    }
}

/// A profile's whole config folder gone from the data folder is no "reset" of every file: the game's copy comes back.
#[test]
fn a_config_folder_removed_from_the_data_folder_comes_back_from_the_game() {
    let fx = Fx::new("folder-gone");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    std::fs::remove_dir_all(fx.data_cfg()).unwrap();
    let back = fx.game_exits();
    assert_eq!((back.copied, back.conflicts.len()), (2, 0), "kiln-auto-loot.json and native.json: {back:?}");
    assert_eq!(read(&fx.data_cfg().join("kiln-auto-loot.json")), DEFAULTS);
}

/// The first copy back after the update to this rule has no record: the newer copy of a file wins (before, the game's
/// always did), and the other is kept beside it, since which side changed is not known.
#[test]
fn without_a_record_the_newer_copy_wins_and_the_other_is_kept() {
    let fx = Fx::new("no-record");
    fx.game_writes("launcher-newer.json", "{ \"x\": \"game\" }\n");
    fx.game_writes("game-newer.json", "{ \"y\": \"game\" }\n");
    fx.game_writes("same.json", "{ \"s\": 1 }\n");
    fx.game_exits();
    std::fs::remove_file(fx.record()).unwrap();
    put(&fx.data_cfg(), "launcher-newer.json", b"{ \"x\": \"launcher\" }\n");
    age(&fx.game_cfg().join("launcher-newer.json"), -60);
    fx.game_writes("game-newer.json", "{ \"y\": \"game, later\" }\n");
    put(&fx.data_cfg(), "game-newer.json", b"{ \"y\": \"launcher\" }\n");
    age(&fx.data_cfg().join("game-newer.json"), -60);
    fx.game_writes("only-in-game.json", "{ \"o\": 1 }\n");
    put(&fx.data_cfg(), "only-in-launcher.json", b"{ \"l\": 1 }\n");
    let a = profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    let said: Vec<&String> = a.warnings.iter().filter(|w| w.contains("no record")).collect();
    assert_eq!(said.len(), 2, "{:?}", a.warnings);
    assert_eq!(read(&fx.data_cfg().join("launcher-newer.json")), "{ \"x\": \"launcher\" }\n");
    assert_eq!(read(&fx.data_cfg().join("game-newer.json")), "{ \"y\": \"game, later\" }\n");
    assert_eq!(read(&fx.conflicts_beside(&fx.data_cfg(), "launcher-newer.json")[0]), "{ \"x\": \"game\" }\n");
    assert_eq!(read(&fx.conflicts_beside(&fx.data_cfg(), "game-newer.json")[0]), "{ \"y\": \"launcher\" }\n");
    assert!(fx.conflicts_beside(&fx.data_cfg(), "same.json").is_empty());
    assert_eq!(read(&fx.data_cfg().join("only-in-game.json")), "{ \"o\": 1 }\n");
    assert_eq!(read(&fx.data_cfg().join("only-in-launcher.json")), "{ \"l\": 1 }\n");
    assert!(fx.record().is_file(), "recorded again");
    assert_eq!(fx.game_exits(), profile::ConfigSync::default());
}

#[test]
fn the_record_is_the_games_copy_and_native_switches_are_in_both() {
    let fx = Fx::new("record");
    fx.game_writes("kiln-auto-loot.json", DEFAULTS);
    profile::activate(&fx.dirs, &fx.pid, &fx.g).unwrap();
    let rec: serde_json::Value = serde_json::from_str(&read(&fx.record())).unwrap();
    assert_eq!(rec["format"], 1);
    let file = &rec["files"]["kiln-auto-loot.json"];
    let (sha, size) = kiln_manage::hash::sha256_file(&fx.game_cfg().join("kiln-auto-loot.json")).unwrap();
    assert_eq!((file["sha256"].as_str(), file["size"].as_u64()), (Some(sha.as_str()), Some(size)));
    assert!(file["mtimeNs"].as_u64().is_some_and(|t| t > 0));
    // the profile's native switches (native.json) are written into the data folder's copy and copied in: both the same
    let native = |d: &Path| read(&d.join("native.json"));
    assert_eq!(native(&fx.game_cfg()), native(&fx.data_cfg()));
    assert!(rec["files"]["native.json"].is_object());
    // a second game in copy mode has its own record of the same profile
    let mut g2 = fx.g.clone();
    g2.id = "g-two".into();
    g2.path = fx.root.join("game2");
    put(&g2.path, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(g2.path.join("kiln")).unwrap();
    profile::activate(&fx.dirs, &fx.pid, &g2).unwrap();
    assert!(profile::config_sync_record(&fx.dirs, &fx.pid, "g-two").is_file());
    assert!(fx.record().is_file());
}
