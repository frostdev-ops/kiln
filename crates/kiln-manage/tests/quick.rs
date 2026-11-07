//! The simple flow (kiln_manage::quick): a dropped mod is imported, added to the game's active profile and written
//! into the game's profile.json in one call; a first-time player gets "My mods"; players with profiles keep theirs;
//! modpacks are left to the consent flow; Undo puts the profile back.

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::profile::{self, Dropins, ProfileMod};
use kiln_manage::quick::{self, DEFAULT_PROFILE};
use kiln_manage::task::NoProgress;
use kiln_manage::ErrorCode;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-quick-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

fn kiln_mod(root: &Path, id: &str, version: &str, extra: &str) -> PathBuf {
    let d = root.join(format!("src/{id}-{version}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "name": "{id} mod", "version": "{version}", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both"{extra} }}"#).as_bytes());
    put(&d, "M.dll", format!("{id} {version}").as_bytes());
    d
}

/// A game folder with Kiln in it (kiln/), not running, copy mode (no prefix).
fn game(root: &Path) -> GameInstall {
    let g = root.join("game");
    put(&g, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(g.join("kiln")).unwrap();
    GameInstall {
        id: "g-quick".into(), kind: GameKind::Client, path: g, exe: "enshrouded.exe".into(), app_id: kiln_manage::CLIENT_APP_ID,
        library: None, platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None,
        path_mode: PathMode::Copy, source: "manual".into(),
    }
}

fn game_mods(g: &GameInstall) -> Vec<(String, String)> {
    let f = profile::read_game_profile(&g.path).unwrap().expect("profile.json");
    f.mods.kiln.iter().filter(|m| m.enabled).map(|m| (m.id.clone(), m.version.clone().unwrap_or_default())).collect()
}

#[test]
fn a_first_time_player_gets_my_mods_with_the_mod_in_the_game() {
    let t = tmp("first");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let m = kiln_mod(&t.0, "night-owl", "1.2.0", "");
    let r = quick::add_with(&dirs, &g, &[m.clone()], false, &mut NoProgress).unwrap();
    assert!(r.created && r.applied, "{r:?}");
    assert_eq!(r.profile_name.as_deref(), Some(DEFAULT_PROFILE));
    assert_eq!(r.profile_id.as_deref(), Some("my-mods"));
    assert_eq!(r.added.len(), 1);
    assert_eq!((r.added[0].id.as_str(), r.added[0].name.as_str()), ("night-owl", "night-owl mod"));
    assert!(r.missing.is_empty() && r.modpacks.is_empty());
    // the game boots it at the next start: profile.json names My mods with the mod, and keeps every drop-in folder
    let f = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(f.id, "my-mods");
    assert_eq!(f.dropins, Dropins::All(true), "what loaded before (the drop-in folders) still loads");
    assert_eq!(game_mods(&g), [("night-owl".to_string(), "1.2.0".to_string())]);
    assert_eq!(profile::active(&dirs, &g).unwrap().unwrap().profile_id, "my-mods");
    // the same drop again: already there, nothing written
    let written = f.generation;
    let again = quick::add_with(&dirs, &g, &[m], false, &mut NoProgress).unwrap();
    assert!(again.added.is_empty() && again.changes.is_empty() && !again.created && !again.applied, "{again:?}");
    assert_eq!(again.already.len(), 1);
    assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().generation, written);
    assert_eq!(profile::list(&dirs).unwrap().len(), 1, "one My mods");
}

#[test]
fn a_new_version_replaces_the_old_one_and_undo_puts_it_back() {
    let t = tmp("undo");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let r1 = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "frost", "0.1.0", "")], false, &mut NoProgress).unwrap();
    let r2 = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "frost", "0.2.0", "")], false, &mut NoProgress).unwrap();
    assert_eq!(r2.changes.len(), 1);
    assert_eq!(r2.changes[0].before.as_ref().map(|b| b.version.as_str()), Some("0.1.0"));
    assert_eq!(game_mods(&g), [("frost".to_string(), "0.2.0".to_string())]);
    // Undo the second drop: 0.1.0 is back (the library keeps 0.2.0)
    let p = quick::revert(&dirs, "my-mods", &r2.changes).unwrap();
    assert_eq!(p.mods.kiln.iter().map(|m| m.version.as_str()).collect::<Vec<_>>(), ["0.1.0"]);
    assert_eq!(library::list(&dirs, Some(ModKind::Kiln)).unwrap()[0].versions.len(), 2);
    profile::refresh_active(&dirs, &g).unwrap();
    assert_eq!(game_mods(&g), [("frost".to_string(), "0.1.0".to_string())]);
    // Undo the first: out of the profile
    let p = quick::revert(&dirs, "my-mods", &r1.changes).unwrap();
    assert!(p.mods.kiln.is_empty());
    // an undo after the entry changed again leaves it alone
    let r3 = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "frost", "0.1.0", "")], false, &mut NoProgress).unwrap();
    assert_eq!(r3.added.len(), 1);
    let stale = quick::revert(&dirs, "my-mods", &r2.changes).unwrap();
    assert_eq!(stale.mods.kiln.iter().map(|m| m.version.as_str()).collect::<Vec<_>>(), ["0.1.0"]);
}

#[test]
fn a_switched_off_mod_is_switched_on() {
    let t = tmp("enable");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let m = kiln_mod(&t.0, "lamp", "1.0.0", "");
    let r = quick::add_with(&dirs, &g, &[m.clone()], false, &mut NoProgress).unwrap();
    let mut p = profile::get(&dirs, "my-mods").unwrap();
    p.mods.kiln[0].enabled = false;
    profile::save(&dirs, &p).unwrap();
    let again = quick::add_with(&dirs, &g, &[m], false, &mut NoProgress).unwrap();
    assert_eq!(again.changes.len(), 1);
    assert_eq!(again.changes[0].before.as_ref().map(|b| b.enabled), Some(false));
    assert!(profile::get(&dirs, "my-mods").unwrap().mods.kiln[0].enabled);
    let back = quick::revert(&dirs, "my-mods", &again.changes).unwrap();
    assert!(!back.mods.kiln[0].enabled, "undo switches it off again");
    assert_eq!(r.added[0].id, "lamp");
}

#[test]
fn players_with_profiles_keep_theirs() {
    let t = tmp("existing");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    // a profile, active in the game: the mod goes into it
    let mut ember = profile::create(&dirs, "Ember run", None).unwrap();
    let r0 = library::import(&dirs, &[kiln_mod(&t.0, "base", "1.0.0", "")], &mut NoProgress).unwrap();
    let base: &LibraryRef = &r0.imported[0];
    ember.mods.kiln.push(ProfileMod { id: base.id.clone(), version: base.version.clone(), hash: base.hash.clone(), enabled: true });
    let ember = profile::save(&dirs, &ember).unwrap();
    profile::activate(&dirs, &ember.id, &g).unwrap();
    let r = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "extra", "1.0.0", "")], false, &mut NoProgress).unwrap();
    assert!(!r.created && r.applied);
    assert_eq!(r.profile_id.as_deref(), Some("ember-run"));
    assert_eq!(game_mods(&g).iter().map(|m| m.0.as_str()).collect::<Vec<_>>(), ["base", "extra"]);
    assert_eq!(profile::list(&dirs).unwrap().len(), 1, "no My mods for them");
    // no profile active in the game: only the library takes it
    profile::deactivate(&dirs, &g).unwrap();
    let r = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "third", "1.0.0", "")], false, &mut NoProgress).unwrap();
    assert_eq!((r.profile_id.as_deref(), r.added.len(), r.import.imported.len()), (None, 0, 1));
    assert!(profile::read_game_profile(&g.path).unwrap().is_none());
    assert!(quick::ensure_default_with(&dirs, &g, false).unwrap().is_none());
    // a profile.json of another data folder is left alone too
    profile::activate(&dirs, &ember.id, &g).unwrap();
    let other = DataDirs::at(t.0.join("other-data"));
    let r = quick::add_with(&other, &g, &[kiln_mod(&t.0, "fourth", "1.0.0", "")], false, &mut NoProgress).unwrap();
    assert!(r.profile_id.is_none() && !r.created);
    assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().id, "ember-run");
}

#[test]
fn while_the_game_runs_the_profile_saves_and_applies_at_the_next_start() {
    let t = tmp("running");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    // a first-time player cannot get My mods activated while the game runs: refused before anything is imported
    let e = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "a", "1.0.0", "")], true, &mut NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning);
    assert!(library::list(&dirs, None).unwrap().is_empty() && profile::list(&dirs).unwrap().is_empty());
    assert_eq!(quick::ensure_default_with(&dirs, &g, true).unwrap_err().code, ErrorCode::GameRunning);
    // with My mods active: saved now, written by the next Play (refresh_active)
    quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "a", "1.0.0", "")], false, &mut NoProgress).unwrap();
    let r = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "b", "1.0.0", "")], true, &mut NoProgress).unwrap();
    assert!(r.running && !r.applied && r.added.len() == 1, "{r:?}");
    assert_eq!(game_mods(&g).len(), 1, "the game folder is not touched while it runs");
    assert!(profile::refresh_active(&dirs, &g).unwrap().is_some());
    assert_eq!(game_mods(&g).len(), 2);
}

#[test]
fn without_kiln_in_the_game_nothing_changes() {
    let t = tmp("nokiln");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    std::fs::remove_dir_all(g.path.join("kiln")).unwrap();
    let e = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "a", "1.0.0", "")], false, &mut NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Unsupported);
    assert!(library::list(&dirs, None).unwrap().is_empty());
    assert_eq!(quick::ensure_default_with(&dirs, &g, false).unwrap_err().code, ErrorCode::Unsupported);
    assert!(profile::list(&dirs).unwrap().is_empty());
}

#[test]
fn missing_requirements_are_named() {
    let t = tmp("missing");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let app = kiln_mod(&t.0, "more-hotbars", "2.1.0", r#", "dependencies": { "ui-lib": "^1.0", "kiln": ">=0.2.0", "extra": { "version": "*", "optional": true } }"#);
    let r = quick::add_with(&dirs, &g, &[app], false, &mut NoProgress).unwrap();
    assert_eq!(r.missing.len(), 1, "{:?}", r.missing);
    assert_eq!((r.missing[0].id.as_str(), r.missing[0].range.as_str(), r.missing[0].by.as_str()), ("ui-lib", "^1.0", "more-hotbars"));
    // dropping the requirement too: nothing missing
    let r = quick::add_with(&dirs, &g, &[kiln_mod(&t.0, "ui-lib", "1.3.0", "")], false, &mut NoProgress).unwrap();
    assert!(r.missing.is_empty());
}

#[test]
fn nothing_importable_leaves_profiles_alone() {
    let t = tmp("nothing");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    put(&t.0, "readme.txt", b"hi");
    let r = quick::add_with(&dirs, &g, &[t.0.join("readme.txt")], false, &mut NoProgress).unwrap();
    assert_eq!(r.import.skipped.len(), 1);
    assert!(r.profile_id.is_none() && !r.created);
    assert!(profile::list(&dirs).unwrap().is_empty(), "no My mods for nothing");
    assert!(profile::read_game_profile(&g.path).unwrap().is_none());
    // a modpack folder is not imported either: its zip goes through the consent flow
    put(&t.0, "pack/kiln-modpack.json", b"{}");
    let r = quick::add_with(&dirs, &g, &[t.0.join("pack")], false, &mut NoProgress).unwrap();
    assert!(r.import.skipped[0].reason.contains("modpack"), "{r:?}");
    assert!(r.modpacks.is_empty());
}

#[cfg(feature = "zip")]
#[test]
fn modpack_zips_are_left_to_the_consent_flow() {
    use std::io::Write;
    let t = tmp("modpack");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let z = t.0.join("ember-isles-3.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&z).unwrap());
    let o = zip::write::SimpleFileOptions::default();
    for (name, data) in [
        ("kiln-modpack.json", br#"{"format":1,"name":"Ember Isles","version":"3","server":{"id":"srv-1"},"mods":[]}"#.to_vec()),
        ("kiln/a/kiln.mod.json", br#"{"id":"a","version":"1.0.0","apiVersion":"0.2","entryAssembly":"A.dll"}"#.to_vec()),
        ("kiln/a/A.dll", b"a".to_vec()),
    ] {
        w.start_file(name, o).unwrap();
        w.write_all(&data).unwrap();
    }
    w.finish().unwrap();
    assert!(quick::is_modpack(&z));
    let m = kiln_mod(&t.0, "lamp", "1.0.0", "");
    let r = quick::add_with(&dirs, &g, &[z.clone(), m], false, &mut NoProgress).unwrap();
    assert_eq!(r.modpacks, [z.display().to_string()]);
    assert_eq!(r.added.iter().map(|a| a.id.as_str()).collect::<Vec<_>>(), ["lamp"], "the plain mod is added, the pack is not");
    assert!(library::list(&dirs, None).unwrap().iter().all(|e| e.id != "a"));
}

#[test]
fn ensure_default_makes_my_mods_once() {
    let t = tmp("default");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0);
    let first = quick::ensure_default_with(&dirs, &g, false).unwrap().unwrap();
    assert!(first.created && first.activation.is_some());
    assert_eq!(first.profile.name, DEFAULT_PROFILE);
    assert!(first.profile.mods.kiln.is_empty());
    assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().id, first.profile.id);
    let second = quick::ensure_default_with(&dirs, &g, false).unwrap().unwrap();
    assert!(!second.created && second.activation.is_none());
    assert_eq!(second.profile.id, first.profile.id);
    assert_eq!(profile::list(&dirs).unwrap().len(), 1);
}

/// The shapes the launcher's TypeScript reads and sends back (launcher/src/api/commands.ts `AddReport`,
/// `ProfileChange`): camelCase keys, a change round-trips as the UI posts it to profile_revert.
#[test]
fn report_and_change_shapes_match_the_frontend() {
    let v = serde_json::to_value(quick::AddReport::default()).unwrap();
    let mut keys: Vec<&str> = v.as_object().unwrap().keys().map(String::as_str).collect();
    keys.sort();
    assert_eq!(keys, ["activation", "added", "already", "applied", "changes", "created", "import", "missing", "modpacks", "profileId", "profileName", "running"]);
    let from_ui = r#"[{"kind":"kiln","before":null,"after":{"id":"night-owl","version":"1.2.0","hash":"kdh1:00","enabled":true}},
        {"kind":"native","before":{"id":"fps","version":"1.0.0","hash":"kdh1:01","enabled":false},"after":{"id":"fps","version":"1.0.0","hash":"kdh1:01","enabled":true}}]"#;
    let changes: Vec<quick::ProfileChange> = serde_json::from_str(from_ui).unwrap();
    assert_eq!((changes[0].kind, changes[1].kind), (ModKind::Kiln, ModKind::Native));
    assert!(changes[0].before.is_none() && !changes[1].before.as_ref().unwrap().enabled);
    let missing = serde_json::to_value(quick::MissingMod { id: "ui-lib".into(), range: "^1.0".into(), by: "more-hotbars".into() }).unwrap();
    assert_eq!(missing, serde_json::json!({ "id": "ui-lib", "range": "^1.0", "by": "more-hotbars" }));
}
