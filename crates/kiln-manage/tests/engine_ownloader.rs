//! A profile's engine mods reach that engine's own loader (engine_mods::place with own_loaders::find): EML's
//! `dbghelp.dll` (or ShroudForge's, which carries EML), a chained Flame loader, Greeneye's proxy, EMF's chained loader.
//! Kiln copies the profile's enabled mods into the folder that loader reads, with its marker, only while the loader is
//! installed, removes its own copies again, and never touches the player's folders.
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::engine_mods;
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::own_loaders;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-ownloader-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

fn eml_src(root: &Path, id: &str) -> PathBuf {
    let d = root.join("src").join(id);
    put(&d, "mod.json", format!(r#"{{"id":"{id}","name":"N","version":"1.0.0","capabilities":["patch"]}}"#).as_bytes());
    put(&d, "src/mod.lua", b"return {}");
    d
}

fn import(dirs: &DataDirs, srcs: &[PathBuf], kind: ModKind, id: &str) -> LibraryRef {
    let r = library::import(dirs, srcs, &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned()
        .unwrap_or_else(|| panic!("{kind:?} {id} not imported: {r:?}"))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

fn eml_loader(game: &Path) {
    put(game, "dbghelp.dll", &build_test_dll(&[], b"EML_LOG_FILE_PATH"));
}

#[test]
fn eml_mods_follow_emls_own_loader() {
    let t = tmp("eml");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let a = import(&dirs, &[eml_src(&t.0, "stacks")], ModKind::Eml, "stacks");
    let b = import(&dirs, &[eml_src(&t.0, "chests")], ModKind::Eml, "chests");
    let mut p = profile::create(&dirs, "eml", None).unwrap();
    p.mods.eml = vec![pm(&a, true), pm(&b, false)];

    // (c) no own loader: nothing placed, report has no note
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert!(rep.placed.is_empty() && rep.own_loaders.is_empty(), "{rep:?}");
    assert!(!game.join("mods/stacks").exists());

    // (a) EML's own dbghelp.dll: the enabled mod is copied with Kiln's marker, the disabled one is not
    eml_loader(&game);
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed, ["eml:stacks"]);
    let m = engine_mods::marker(&game.join("mods/stacks")).unwrap();
    assert_eq!((m.kind, m.id.as_str()), (ModKind::Eml, "stacks"));
    assert!(!game.join("mods/chests").exists());
    assert_eq!(rep.own_loaders.len(), 1);
    assert_eq!(rep.own_loaders[0].copied, ["stacks"]);
    assert!(rep.own_loaders[0].text.contains("EML's own loader (dbghelp.dll) is installed"), "{}", rep.own_loaders[0].text);
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().kept, ["eml:stacks"]);

    // switched on / off
    p.mods.eml[1].enabled = true;
    p.mods.eml[0].enabled = false;
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!((rep.placed.clone(), rep.removed.clone()), (vec!["eml:chests".to_string()], vec!["eml:stacks".to_string()]));
    assert!(!game.join("mods/stacks").exists());

    // gone from the profile
    p.mods.eml.truncate(1);
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.removed, ["eml:chests"]);

    // loader removed: Kiln's copies go too
    p.mods.eml = vec![pm(&a, true)];
    engine_mods::place(&dirs, &p, &game).unwrap();
    assert!(game.join("mods/stacks").is_dir());
    std::fs::remove_file(game.join("dbghelp.dll")).unwrap();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.removed, ["eml:stacks"]);
    assert!(!game.join("mods/stacks").exists());
}

#[test]
fn cleanup_reports_changed_eml_copies_as_potentially_still_running() {
    for rel in ["src/mod.lua", "mod.json", "native-plugin.ini", "addon.dll", "player-settings.json"] {
        let t = tmp(&format!("cleanup-{}", rel.replace('/', "-")));
        let dirs = DataDirs::at(t.0.join("data"));
        let game = t.0.join("game");
        std::fs::create_dir_all(game.join("kiln")).unwrap();
        eml_loader(&game);
        let src = eml_src(&t.0, "changed");
        put(&src, "native-plugin.ini", b"[plugin]\npath=addon.dll\n");
        put(&src, "addon.dll", b"original shipped plugin");
        let changed = import(&dirs, &[src], ModKind::Eml, "changed");
        let clean = import(&dirs, &[eml_src(&t.0, "clean")], ModKind::Eml, "clean");
        let mut p = profile::create(&dirs, "eml cleanup", None).unwrap();
        p.mods.eml = vec![pm(&changed, true), pm(&clean, true)];
        engine_mods::place(&dirs, &p, &game).unwrap();
        put(&game.join("mods/changed"), rel, b"player-owned edit");
        let report = engine_mods::unplace(&dirs, &game).unwrap();
        assert_eq!(report.removed, [game.join("mods/clean")]);
        assert_eq!(report.retained.len(), 1);
        assert_eq!(report.retained[0].path, game.join("mods/changed"));
        assert!(report.retained[0].may_run, "foreign EML can still discover this folder");
        assert!(report.warning().unwrap().contains("may still run through Kiln or another loader"));
        assert_eq!(std::fs::read(game.join("mods/changed").join(rel)).unwrap(), b"player-owned edit");
        assert!(game.join("dbghelp.dll").is_file(), "the player's loader is not a profile placement");
    }
}

#[test]
fn player_folders_are_never_touched() {
    let t = tmp("player");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    eml_loader(&game);
    let a = import(&dirs, &[eml_src(&t.0, "stacks")], ModKind::Eml, "stacks");
    let mut p = profile::create(&dirs, "player", None).unwrap();
    p.mods.eml = vec![pm(&a, true)];
    // the player's own folder under the same name
    put(&game, "mods/stacks/mod.json", br#"{"id":"stacks","name":"Mine","version":"0.9.0"}"#);
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.own, ["eml:stacks"]);
    assert!(engine_mods::marker(&game.join("mods/stacks")).is_none());
    std::fs::remove_dir_all(game.join("mods/stacks")).unwrap();
    // the same mod under another folder name: real EML stops on a duplicate id, so Kiln does not add a second copy
    put(&game, "mods/Stacks Mod/mod.json", br#"{"id":"stacks","name":"Mine","version":"0.9.0"}"#);
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.own, ["eml:stacks"]);
    assert!(!game.join("mods/stacks").exists());
    // the profile switching it off never removes the player's folder
    p.mods.eml[0].enabled = false;
    engine_mods::place(&dirs, &p, &game).unwrap();
    assert!(game.join("mods/Stacks Mod/mod.json").is_file());
    std::fs::remove_dir_all(game.join("mods/Stacks Mod")).unwrap();
    // a file the mod wrote into its copy is the player's: the copy stays Kiln's, the file is kept
    p.mods.eml[0].enabled = true;
    engine_mods::place(&dirs, &p, &game).unwrap();
    put(&game, "mods/stacks/settings.txt", b"written by the player");
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!((rep.kept.as_slice(), rep.changed.len()), (&["eml:stacks".to_string()][..], 0), "{rep:?}");
    p.mods.eml.clear();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.removed, ["eml:stacks"]);
    assert!(!game.join("mods/stacks").exists());
    assert_eq!(std::fs::read(game.join("kiln/kept-mod-files/eml/stacks/settings.txt")).unwrap(), b"written by the player");
    assert!(rep.kept_files[0].contains("settings.txt"), "{rep:?}");
    // an edited library file still makes the copy the player's: left as it is, neither refreshed nor removed
    p.mods.eml = vec![pm(&a, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(std::fs::read(game.join("mods/stacks/settings.txt")).unwrap(), b"written by the player", "{rep:?}");
    assert!(!game.join("kiln/kept-mod-files/eml/stacks").exists());
    put(&game, "mods/stacks/mod.json", br#"{"id":"stacks","name":"Edited","version":"1.0.0"}"#);
    p.mods.eml.clear();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.changed, ["eml:stacks"]);
    assert!(game.join("mods/stacks/settings.txt").is_file());
}

#[test]
fn placed_folders_use_the_library_id_not_the_manifest_id() {
    let t = tmp("slug");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    eml_loader(&game);
    let a = import(&dirs, &[eml_src(&t.0, "hhelper")], ModKind::Eml, "hhelper");
    // A Library entry whose id is a slug of the mod's own id (WP import): same bytes, another id.
    let from = library::path_of(&dirs, &a).unwrap();
    let slug = LibraryRef { id: "hoarders-helper".into(), ..a.clone() };
    let to = dirs.library_entry(ModKind::Eml, &slug.id, &slug.version, &slug.hash).unwrap();
    kiln_manage::util::copy_tree(&from, &to).unwrap();
    let mut p = profile::create(&dirs, "slug", None).unwrap();
    p.mods.eml = vec![pm(&slug, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed, ["eml:hoarders-helper"]);
    assert_eq!(engine_mods::marker(&game.join("mods/hoarders-helper")).unwrap().id, "hoarders-helper");
    assert!(!game.join("mods/hhelper").exists());
}

#[test]
fn shroudforge_loader_hosted_or_own_takes_eml_mods_into_its_folder() {
    let t = tmp("sf");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln/plugins")).unwrap();
    let a = import(&dirs, &[eml_src(&t.0, "stacks")], ModKind::Eml, "stacks");
    let mut p = profile::create(&dirs, "sf", None).unwrap();
    p.mods.eml = vec![pm(&a, true)];
    // ShroudForge's own dbghelp.dll, its mods folder configured elsewhere
    put(&game, "dbghelp.dll", &build_test_dll(&[], b"EML_LOG_FILE_PATH shroudforge"));
    put(&game, "shroudforge/config/modloader-config.json", br#"{"paths":{"mods":"sfmods"}}"#);
    std::fs::create_dir_all(game.join("sfmods")).unwrap();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed, ["eml:stacks"]);
    assert!(engine_mods::marker(&game.join("sfmods/stacks")).is_some());
    assert!(rep.own_loaders.iter().any(|n| n.loader.kind == ModKind::Eml && n.text.starts_with("ShroudForge's loader (dbghelp.dll) is installed: it carries EML")),
        "{:?}", rep.own_loaders);
    // Kiln hosting ShroudForge for this profile (no SF proxy): ShroudForge owns startup and writes the EML mods of its folder
    std::fs::remove_file(game.join("dbghelp.dll")).unwrap();
    let view = own_loaders::ProfileView { mods: vec![(ModKind::ShroudForge, "x".into(), true)], dropins_shroudforge: false, ..Default::default() };
    put(&game, "kiln/plugins/kiln-shroudforge.dll", b"x");
    put(&game, "kiln/plugins/shroudforge/shroudforge-runtime.dll", b"x");
    let l = own_loaders::find(&game, Some(&view));
    assert_eq!(l[0].via, own_loaders::Via::HostedShroudForge);
    assert_eq!(l[0].root(&game), game.join("sfmods"));
}

#[test]
fn chained_flame_greeneye_and_emf_loaders() {
    let t = tmp("others");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let fl = t.0.join("src/creative");
    put(&fl, "mod.json", br#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#);
    put(&fl, "flame.json", br#"{"dll":"creative.dll"}"#);
    put(&fl, "creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    let ge = t.0.join("src/worstmap");
    put(&ge, "mod.json", br#"{"id":"worstmap","name":"Map","version":"1.0.0","dll":"map.dll","side":"client"}"#);
    put(&ge, "map.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    let f = import(&dirs, &[fl], ModKind::Flame, "creative");
    let g = import(&dirs, &[ge], ModKind::Greeneye, "worstmap");
    let mut p = profile::create(&dirs, "others", None).unwrap();
    p.mods.flame = vec![pm(&f, true)];
    p.mods.greeneye = vec![pm(&g, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    // Greeneye mods run from greeneye/<id>/ and Flame mods from mods/<id>/ with or without their engine's loader (they
    // read and write their own files there)
    assert!(engine_mods::marker(&game.join("mods/creative")).is_some() && engine_mods::marker(&game.join("greeneye/worstmap")).is_some());
    assert_eq!(rep.placed, ["greeneye:worstmap", "flame:creative"]);
    // Flame's dinput8.dll chained by the installer, Greeneye's dbgcore.dll next to the game: the same copies serve them
    put(&game, "kiln/chain/flame-dinput8.dll", &build_test_dll(&[], b"FlameStartRuntime flame.json"));
    put(&game, "kiln/chain.ini", b"[chain]\nchain\\flame-dinput8.dll\n");
    put(&game, "dbgcore.dll", &build_test_dll(&[], b"greeneye"));
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!((rep.placed.clone(), rep.kept.clone()), (Vec::<String>::new(), vec!["greeneye:worstmap".to_string(), "flame:creative".to_string()]));
    assert!(rep.own_loaders.iter().any(|n| n.loader.kind == ModKind::Flame && n.copied == ["creative"]), "{rep:?}");
    // the chain line switched off: both copies stay, the profile still has them on
    put(&game, "kiln/chain.ini", b"[chain]\n;chain\\flame-dinput8.dll\n");
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!((rep.removed.clone(), rep.kept.clone()), (Vec::<String>::new(), vec!["greeneye:worstmap".to_string(), "flame:creative".to_string()]));
    // switched off in the profile: Flame's copy goes
    p.mods.flame[0].enabled = false;
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.removed, ["flame:creative"]);
    assert!(!game.join("mods/creative").exists());
}

fn game_with_z(t: &Path) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    GameInstall {
        id: "g-own".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into(),
    }
}

#[test]
fn activation_reports_and_play_once_keeps_the_fixed_folder() {
    let t = tmp("activate");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = game_with_z(&t.0);
    eml_loader(&game.path);
    let a = import(&dirs, &[eml_src(&t.0, "stacks")], ModKind::Eml, "stacks");
    let b = import(&dirs, &[eml_src(&t.0, "chests")], ModKind::Eml, "chests");
    let mut p = profile::create(&dirs, "act", None).unwrap();
    p.mods.eml = vec![pm(&a, true)];
    profile::save(&dirs, &p).unwrap();
    let act = profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(act.own_loaders.len(), 1);
    assert_eq!(act.own_loaders[0].copied, ["stacks"]);
    assert!(game.path.join("mods/stacks").is_dir());
    // profile.json still names the Library folder: kiln-eml runs it from there when the loader does not run
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    assert!(!file.mods.eml[0].path.contains("mods\\stacks") && !file.mods.eml[0].path.ends_with("mods/stacks"), "{}", file.mods.eml[0].path);
    let st = engine_mods::status(&game.path);
    let eml = st.iter().find(|s| s.kind == ModKind::Eml).unwrap();
    assert_eq!(eml.own_loader.as_ref().unwrap().copied, ["stacks"]);
    assert_eq!(profile::active(&dirs, &game).unwrap().unwrap().own_loaders.len(), 1);
    // play-once with another EML selection: refused while EML's own loader reads mods/
    let mut q = profile::create(&dirs, "once", None).unwrap();
    q.mods.eml = vec![pm(&b, true)];
    profile::save(&dirs, &q).unwrap();
    let e = profile::write_once(&dirs, &q.id, &game).unwrap_err();
    assert!(e.message.contains("EML's own loader reads its mods from mods/ itself"), "{}", e.message);
    // without the loader the profile paths are enough
    std::fs::remove_file(game.path.join("dbghelp.dll")).unwrap();
    profile::write_once(&dirs, &q.id, &game).unwrap();
}

#[test]
fn notes_serialize_for_the_launcher() {
    let t = tmp("json");
    let game = t.0.join("game");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    eml_loader(&game);
    let n = own_loaders::notes(&game, &own_loaders::find(&game, None), None);
    let v = serde_json::to_value(&n).unwrap();
    assert_eq!(v[0]["kind"], "eml");
    assert_eq!(v[0]["file"], "dbghelp.dll");
    assert_eq!(v[0]["via"], "proxy");
    assert_eq!(v[0]["delivery"], "placed");
    assert_eq!(v[0]["notCopied"], serde_json::json!([]));
    let back: Vec<own_loaders::OwnLoaderNote> = serde_json::from_value(v).unwrap();
    assert_eq!(back, n);
}
