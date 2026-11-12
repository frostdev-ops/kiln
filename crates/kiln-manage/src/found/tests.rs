//! The game-folder scan with a fake game folder per engine, and profile.json `dropins` for old and new profiles.

use super::*;
use crate::game::{GameKind, PathMode, Platform};
use crate::pe::build_test_dll;
use crate::profile::{Dropins, ProfileMods};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-found-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// A client folder; `kiln`: with Kiln's dinput8.dll.
fn game(t: &Path, kiln: bool) -> GameInstall {
    let g = t.join("game");
    put(&g, "enshrouded.exe", b"MZ");
    if kiln {
        put(&g, "dinput8.dll", &build_test_dll(&["KilnProxyInfo", "DirectInput8Create"], b""));
        std::fs::create_dir_all(g.join("kiln")).unwrap();
    }
    GameInstall {
        id: "g-found".into(), kind: GameKind::Client, path: g, exe: "enshrouded.exe".into(), app_id: crate::CLIENT_APP_ID,
        library: None, platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None,
        path_mode: PathMode::Copy, source: "manual".into(),
    }
}

fn profile(dropins: &[ModKind], engines: bool) -> Profile {
    Profile {
        format: 1, id: "p".into(), name: "P".into(), created: String::new(), updated: String::new(), generation: 1,
        notes: String::new(), mods: ProfileMods::default(), dropins: dropins.to_vec(), dropins_engines: engines,
        data_dir: Default::default(), server: None, late: vec![],
    }
}

fn pm(id: &str) -> ProfileMod {
    ProfileMod { id: id.into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "a".repeat(64)), enabled: true }
}

/// Hoarder's Helper as its zip ships it: `mods/HoardersHelper/mod.json` + `src/mod.lua`.
fn eml_mod(g: &Path, folder: &str) {
    put(g, &format!("mods/{folder}/mod.json"), format!(r#"{{"id":"{folder}","name":"{folder} mod","version":"1.0.1"}}"#).as_bytes());
    put(g, &format!("mods/{folder}/src/mod.lua"), b"return {}");
}

fn one<'a>(r: &'a FoundReport, path: &str) -> &'a FoundMod {
    r.mods.iter().find(|m| m.path == path).unwrap_or_else(|| panic!("{path} not found: {r:#?}"))
}

#[test]
fn eml_mod_follows_eml_dropins() {
    let t = tmp("eml");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    eml_mod(&g.path, "HoardersHelper");
    let off = scan(&dirs, &g, &profile(&[], true));
    let m = one(&off, "mods/HoardersHelper");
    assert_eq!((m.kind, m.state, m.fix), (Some(ModKind::Eml), FoundState::Off, Some(FoundFix::Dropins)), "{m:?}");
    assert_eq!(m.name, "HoardersHelper mod");
    assert_eq!(m.version.as_deref(), Some("1.0.1"));
    assert!(m.reason.contains("EML drop-ins are off"), "{}", m.reason);
    assert_eq!(off.off(), 1);
    // the fix the launcher offers: EML's switch on
    let on = scan(&dirs, &g, &with_dropin(&profile(&[], true), ModKind::Eml));
    let m = one(&on, "mods/HoardersHelper");
    assert_eq!((m.state, m.via), (FoundState::Loads, Some(FoundVia::Kiln)), "{m:?}");
    // an old profile with the four Kiln kinds on reads every folder, as before
    let old = scan(&dirs, &g, &profile(&ModKind::CORE, false));
    assert_eq!(one(&old, "mods/HoardersHelper").state, FoundState::Loads);
    // a new profile with exactly those four on does not read EML's folder
    let core = scan(&dirs, &g, &profile(&ModKind::CORE, true));
    assert_eq!(one(&core, "mods/HoardersHelper").state, FoundState::Off);
}

#[test]
fn own_loader_runs_its_folder() {
    let t = tmp("own");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    eml_mod(&g.path, "ChestSlots");
    put(&g.path, "dbghelp.dll", &build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH"));
    // on Windows the game imports dbghelp.dll, so EML's loader runs and reads mods/ itself
    let mut win = g.clone();
    win.platform = Platform::Windows;
    let r = scan(&dirs, &win, &profile(&[], true));
    assert!(r.loaders.iter().any(|l| l.engine == Engine::Eml && l.file == "dbghelp.dll" && l.kinds == [ModKind::Eml]), "{:?}", r.loaders);
    let m = one(&r, "mods/ChestSlots");
    assert_eq!((m.state, m.via), (FoundState::Loads, Some(FoundVia::OwnLoader)), "{m:?}");
    assert!(m.reason.contains("dbghelp.dll"), "{}", m.reason);
    // under Proton it runs only with a DLL override the launcher cannot see: without EML drop-ins nothing is promised
    let r = scan(&dirs, &g, &profile(&[], true));
    let m = one(&r, "mods/ChestSlots");
    assert_eq!((m.state, m.via, m.fix), (FoundState::Off, None, Some(FoundFix::Dropins)), "{m:?}");
    assert!(m.reason.contains("dbghelp.dll") && m.reason.contains("DLL override"), "{}", m.reason);
    // with EML drop-ins on it loads either way: by Kiln, or by EML's loader when the override is set
    let r = scan(&dirs, &g, &profile(&[ModKind::Eml], true));
    let m = one(&r, "mods/ChestSlots");
    assert_eq!((m.state, m.via), (FoundState::Loads, Some(FoundVia::Kiln)), "{m:?}");
    assert!(m.reason.contains("dbghelp.dll"), "{}", m.reason);
    // a loader Kiln's installer moved into kiln/chain/ counts too
    std::fs::remove_file(g.path.join("dbghelp.dll")).unwrap();
    put(&g.path, "kiln/chain/eml-dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"EML_LOG_FILE_PATH"));
    put(&g.path, "kiln/chain.ini", b"[chain]\r\nchain\\eml-dinput8.dll\r\n");
    let r = scan(&dirs, &g, &profile(&[], true));
    assert_eq!(r.loaders.iter().map(|l| l.file.as_str()).collect::<Vec<_>>(), ["kiln/chain/eml-dinput8.dll"]);
    assert_eq!(one(&r, "mods/ChestSlots").via, Some(FoundVia::OwnLoader));
}

#[test]
fn kiln_copies_and_kiln_files_are_skipped() {
    let t = tmp("skip");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    put(&g.path, "shrouded_engine_tweaks/mods/fly/init.lua", b"");
    put(&g.path, "shrouded_engine_tweaks/mods/fly/.kiln-library.json", b"{}");
    put(&g.path, "shrouded_engine_tweaks/mods/own/init.lua", b"");
    put(&g.path, "mods/you.pack/pack.toml", b"id = \"you.pack\"\nversion = \"1.0.0\"\n");
    put(&g.path, "mods/you.pack/.kiln-managed", b"");
    put(&g.path, "mods/enshwick/enshwick.dll", b"MZ");
    put(&g.path, "mods/.hidden/mod.json", b"{}");
    put(&g.path, "kiln/plugins/kiln-eml.dll", &build_test_dll(&["kiln_plugin_main"], b""));
    put(&g.path, "kiln/plugins/my-plugin.dll", &build_test_dll(&["kiln_plugin_main"], b""));
    put(&g.path, "mods/readme.txt", b"extract into the game folder");
    let r = scan(&dirs, &g, &profile(&[], true));
    let paths: Vec<&str> = r.mods.iter().map(|m| m.path.as_str()).collect();
    assert_eq!(paths, ["kiln/plugins/my-plugin.dll", "shrouded_engine_tweaks/mods/own"], "{r:#?}");
    let set = one(&r, "shrouded_engine_tweaks/mods/own");
    assert_eq!((set.kind, set.state), (Some(ModKind::Set), FoundState::Loads), "SET always reads its folder");
    let plugin = one(&r, "kiln/plugins/my-plugin.dll");
    assert_eq!((plugin.kind, plugin.state, plugin.fix), (Some(ModKind::Plugin), FoundState::Off, Some(FoundFix::Dropins)));
}

#[test]
fn compat_engines_read_their_folder_unless_the_profile_lists_the_engine() {
    let t = tmp("ge");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    put(&g.path, "greeneye/zoom/mod.json", br#"{"id":"zoom","name":"Zoom Out","version":"1.0.0","dll":"zoom.dll"}"#);
    put(&g.path, "greeneye/zoom/zoom.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    // a profile from before dev.22 with no Greeneye mods of its own: kiln-greeneye read the folder then, and still does
    let mut p = profile(&[], false);
    let m = scan(&dirs, &g, &p);
    let z = one(&m, "greeneye/zoom");
    assert_eq!((z.kind, z.state), (Some(ModKind::Greeneye), FoundState::Loads), "{z:?}");
    assert!(p.dropins_file().names(ModKind::Greeneye));
    // ... and stopped reading it once the profile had Greeneye mods of its own
    p.mods.greeneye.push(pm("other"));
    let z = scan(&dirs, &g, &p).mods.remove(0);
    assert_eq!((z.state, z.fix), (FoundState::Off, Some(FoundFix::Dropins)), "{z:?}");
    // a dev.22 profile: Greeneye's switch decides, whatever the profile lists
    let mut p = profile(&[], true);
    let z = scan(&dirs, &g, &p).mods.remove(0);
    assert_eq!((z.state, z.fix), (FoundState::Off, Some(FoundFix::Dropins)), "{z:?}");
    p.dropins = vec![ModKind::Greeneye];
    p.mods.greeneye.push(pm("other"));
    let z = scan(&dirs, &g, &p).mods.remove(0);
    assert_eq!(z.state, FoundState::Loads, "{z:?}");
    // the same mod in the profile: the profile's copy wins
    p.mods.greeneye.push(pm("zoom"));
    let z = scan(&dirs, &g, &p).mods.remove(0);
    assert_eq!((z.state, z.profile_mod.as_deref()), (FoundState::InProfile, Some("zoom")), "{z:?}");
}

#[test]
fn same_eml_id_in_the_profile_and_wrong_folders() {
    let t = tmp("same");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    eml_mod(&g.path, "XHL-Max-Stack");
    put(&g.path, "mods/beast/mod.json", br#"{"id":"beast","name":"Beast","version":"1.0.0","emf":27,"dll":"beast.dll"}"#);
    put(&g.path, "mods/beast/beast.dll", &build_test_dll(&["emf_mod_load"], b""));
    let mut p = profile(&ModKind::ALL, true);
    p.mods.eml.push(pm("xhl-max-stack"));
    let r = scan(&dirs, &g, &p);
    assert_eq!(one(&r, "mods/XHL-Max-Stack").state, FoundState::InProfile);
    let b = one(&r, "mods/beast");
    assert_eq!((b.kind, b.state, b.fix), (Some(ModKind::Emf), FoundState::Off, Some(FoundFix::Add)), "{b:?}");
    assert!(b.reason.contains("emf/mods"), "{}", b.reason);
}

#[test]
fn shroudforge_follows_its_dropins_or_a_profile_mod() {
    let t = tmp("sf");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    put(&g.path, "mods/sfpack/mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"1.0.0"}"#);
    put(&g.path, "mods/sfpack/extended.mod.json", br#"{"launcher":"SF"}"#);
    put(&g.path, "mods/sfpack/src/mod.lua", b"");
    let mut p = profile(&[], true);
    let m = scan(&dirs, &g, &p).mods.remove(0);
    assert_eq!((m.kind, m.state, m.fix), (Some(ModKind::ShroudForge), FoundState::Off, Some(FoundFix::Dropins)), "{m:?}");
    p.mods.shroudforge.push(pm("other"));
    assert_eq!(scan(&dirs, &g, &p).mods[0].state, FoundState::Loads);
}

#[test]
fn no_kiln_missing_and_unreadable_folders_do_not_fail() {
    let t = tmp("nokiln");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, false);
    let empty = scan(&dirs, &g, &profile(&[], true));
    assert!(!empty.kiln_installed && empty.mods.is_empty() && empty.unreadable.is_empty(), "{empty:?}");
    eml_mod(&g.path, "Ember");
    let r = scan(&dirs, &g, &profile(&ModKind::ALL, true));
    let m = one(&r, "mods/Ember");
    assert_eq!((m.state, m.fix), (FoundState::Off, None), "{m:?}");
    assert!(m.reason.contains("not installed"));
    // a folder that cannot be listed, and a link
    use std::os::unix::fs::PermissionsExt;
    put(&g.path, "greeneye/x/mod.json", b"{}");
    std::fs::set_permissions(g.path.join("greeneye"), std::fs::Permissions::from_mode(0o000)).unwrap();
    std::os::unix::fs::symlink("/", g.path.join("mods/link")).unwrap();
    let root_reads = std::fs::read_dir(g.path.join("greeneye")).is_ok(); // root reads everything
    let r = scan(&dirs, &g, &profile(&[], true));
    std::fs::set_permissions(g.path.join("greeneye"), std::fs::Permissions::from_mode(0o755)).unwrap();
    assert!(root_reads || r.unreadable.iter().any(|u| u.path == "greeneye"), "{r:?}");
    assert_eq!(one(&r, "mods/link").state, FoundState::CannotRun);
}

#[test]
fn ember_and_unknown_cannot_run() {
    let t = tmp("ember");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    put(&g.path, "ember/mods/chests/chests.dll", &build_test_dll(&["init"], b""));
    put(&g.path, "mods/stuff/notes.txt", b"");
    let r = scan(&dirs, &g, &profile(&ModKind::ALL, true));
    assert_eq!(one(&r, "ember/mods/chests").state, FoundState::CannotRun);
    assert_eq!(one(&r, "mods/stuff").state, FoundState::CannotRun);
}

#[test]
fn add_imports_into_a_profile_that_is_not_active() {
    let t = tmp("add");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, true);
    put(&g.path, "greeneye/zoom/mod.json", br#"{"id":"zoom","name":"Zoom Out","version":"1.0.0","dll":"zoom.dll"}"#);
    put(&g.path, "greeneye/zoom/zoom.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    let p = crate::profile::create(&dirs, "Side", None).unwrap();
    let r = add(&dirs, &g, &p.id, "greeneye/zoom", &mut crate::task::NoProgress).unwrap();
    assert_eq!(r.added.iter().map(|a| (a.kind, a.id.as_str())).collect::<Vec<_>>(), [(ModKind::Greeneye, "zoom")], "{r:?}");
    let saved = crate::profile::get(&dirs, &p.id).unwrap();
    assert_eq!(saved.mods.greeneye.len(), 1);
    assert!(g.path.join("greeneye/zoom/zoom.dll").is_file(), "the player's folder stays");
    assert_eq!(scan(&dirs, &g, &saved).mods[0].state, FoundState::InProfile);
    // only what the scan lists
    assert_eq!(add(&dirs, &g, &p.id, "../etc", &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::NotFound);
}

// ---- profile.json `dropins` ----

#[test]
fn dropins_of_old_profiles_are_written_as_before() {
    // The engine plugins that read their folder without asking `dropins` before dev.22 (Enshwick always; Greeneye,
    // Flame, Shroudtopia, shroudlink and Ember while the profile listed no mod of theirs) are named in the list now,
    // so an old profile loads the same folders under the one rule.
    use ModKind as K;
    let unasked = [K::Greeneye, K::Flame, K::Shroudtopia, K::Enshwick, K::Shroudlink, K::Ember];
    let with = |own: &[ModKind]| ModKind::ALL.into_iter().filter(|k| own.contains(k) || unasked.contains(k)).collect::<Vec<_>>();
    for (kinds, want) in [
        (vec![], Dropins::Kinds(with(&[]))),
        (vec![K::Plugin], Dropins::Kinds(with(&[K::Plugin]))),
        (K::CORE.to_vec(), Dropins::All(true)),
        (K::ALL.to_vec(), Dropins::All(true)),
        (vec![K::Kiln, K::Eml], Dropins::Kinds(with(&[K::Kiln, K::Eml]))),
    ] {
        assert_eq!(profile(&kinds, false).dropins_file(), want, "{kinds:?}");
    }
    // a profile with Flame mods of its own did not read Flame's folder: it still does not
    let mut p = profile(&[], false);
    p.mods.flame.push(pm("lamp"));
    assert!(!p.dropins_file().names(K::Flame) && p.dropins_file().names(K::Greeneye) && !p.dropins_file().names(K::Eml));
    assert!(!p.dropin_kinds().contains(&K::Flame) && p.dropin_kinds().contains(&K::Enshwick));
    // an old meta.json reads with dropinsEngines off, and writes back without it
    let old = r#"{"format":1,"id":"p","name":"P","created":"","updated":"","generation":3,"mods":{"kiln":[]},"dropins":["kiln","native","plugin","emf"]}"#;
    let p: Profile = serde_json::from_str(old).unwrap();
    assert!(!p.dropins_engines);
    assert_eq!(p.dropin_kinds(), ModKind::ALL.into_iter().filter(|k| *k != K::Xhl).collect::<Vec<_>>());
    assert!(!serde_json::to_string(&p).unwrap().contains("dropinsEngines"));
}

#[test]
fn external_xhl_companions_never_get_a_kiln_dropin_switch() {
    for modern in [false, true] {
        for kinds in [vec![], ModKind::CORE.to_vec(), ModKind::ALL.to_vec(), vec![ModKind::Xhl]] {
            let p = profile(&kinds, modern);
            assert!(!p.dropin_kinds().contains(&ModKind::Xhl));
            assert!(!matches!(p.dropins_file(), Dropins::Kinds(ref ks) if ks.contains(&ModKind::Xhl)));
            let switched = with_dropin(&p, ModKind::Xhl);
            assert!(!switched.dropins.contains(&ModKind::Xhl));
            assert_eq!(switched.dropin_kinds(), p.dropin_kinds());
        }
    }
}

#[test]
fn dropins_with_engine_switches_round_trip() {
    use ModKind as K;
    let all = crate::profile::DROPIN_KINDS.to_vec();
    for (kinds, want) in [
        (vec![], Dropins::All(false)),
        (K::CORE.to_vec(), Dropins::Kinds(K::CORE.to_vec())),
        (all.clone(), Dropins::All(true)),
        (K::ALL.to_vec(), Dropins::All(true)),
        // kinds whose plugin does not read dropins are left out of the file
        (vec![K::Set, K::Ember], Dropins::All(false)),
        (vec![K::Set, K::Enshwick, K::Ember], Dropins::Kinds(vec![K::Enshwick])),
        (vec![K::Greeneye, K::Kiln, K::Set], Dropins::Kinds(vec![K::Kiln, K::Greeneye])),
    ] {
        let p = profile(&kinds, true);
        let f = p.dropins_file();
        assert_eq!(f, want, "{kinds:?}");
        // the meta.json and the file value both read back the same
        let back: Profile = serde_json::from_str(&serde_json::to_string(&p).unwrap()).unwrap();
        assert_eq!(back, p);
        let fv: Dropins = serde_json::from_str(&serde_json::to_string(&f).unwrap()).unwrap();
        assert_eq!(fv, f);
        // what each reader makes of it
        for k in [K::Kiln, K::Emf, K::Eml, K::ShroudForge] {
            assert_eq!(f.names(k), kinds.contains(&k) || (f.is_all()), "{kinds:?} {k:?}");
        }
    }
    let mut p = profile(&all[..all.len() - 1], true);
    assert!(!p.dropins_file().is_all());
    p = with_dropin(&p, *all.last().unwrap());
    assert!(p.dropins_file().is_all() && p.dropins_engines);
}
