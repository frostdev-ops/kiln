//! Other engines' mods managed like Kiln mods (engine_mods): Library import of every kind Kiln runs, profiles that list
//! them (profile.json mods.<kind>), ShroudForge/SET placement in their engine's folder, settings, EML's restore, and
//! modpacks that carry them with their kdh1.
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::engine_mods;
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::{Clients, ModKind};
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{hash, modpack};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-engine-mods-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// One mod of each kind Kiln runs, as their authors ship them.
fn sources(root: &Path) -> Vec<PathBuf> {
    let eml = root.join("src/stacks");
    put(&eml, "mod.json", br#"{"id":"stacks","name":"Bigger Stacks","version":"1.2.0","capabilities":["patch"]}"#);
    put(&eml, "src/mod.lua", b"return {}");
    let sf = root.join("src/sfpack");
    put(&sf, "mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"1.0.0"}"#);
    put(&sf, "extended.mod.json", br#"{"launcher":"SF","enabled":false,"targets":["client","server"],
        "settings":{"speed":{"label":"Speed","control":"slider","min":1,"max":10,"value":3}}}"#);
    put(&sf, "src/mod.lua", b"return {}");
    let set = root.join("src/flymode");
    put(&set, "init.lua", b"print('fly')");
    let ge = root.join("src/worstmap");
    put(&ge, "mod.json", br#"{"id":"worstmap","name":"The Worst Map Mod","version":"1.0.0","dll":"map.dll","side":"client"}"#);
    put(&ge, "map.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    let flame = root.join("src/creative");
    put(&flame, "mod.json", br#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#);
    put(&flame, "flame.json", br#"{"dll":"creative.dll"}"#);
    put(&flame, "creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    let st = root.join("src/minimap.dll");
    put(root, "src/minimap.dll", &build_test_dll(&["CreateModInstance"], b""));
    vec![eml, sf, set, ge, flame, st]
}

fn refs(r: &library::ImportReport, kind: ModKind, id: &str) -> LibraryRef {
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned().unwrap_or_else(|| panic!("{kind:?} {id} not imported: {r:?}"))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

fn game_with_z(t: &Path) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into(),
    }
}

#[test]
fn every_engine_kind_is_imported_profiled_placed_and_rendered() {
    let t = tmp("flow");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    assert!(r.skipped.is_empty(), "{:?}", r.skipped);
    let eml = refs(&r, ModKind::Eml, "stacks");
    let sf = refs(&r, ModKind::ShroudForge, "sfpack");
    let set = refs(&r, ModKind::Set, "flymode");
    let ge = refs(&r, ModKind::Greeneye, "worstmap");
    let flame = refs(&r, ModKind::Flame, "creative");
    let st = refs(&r, ModKind::Shroudtopia, "minimap");
    // content addressed like Kiln mods: library/<kind>/<id>/<version>+<hash8>, kdh1 of the folder
    for x in [&eml, &sf, &set, &ge, &flame, &st] {
        let folder = library::path_of(&dirs, x).unwrap();
        assert!(folder.starts_with(dirs.library().join(x.kind.as_str())), "{}", folder.display());
        assert_eq!(hash::dirhash(&folder).unwrap(), x.hash);
        let rec = library::record(&dirs, x).unwrap();
        assert!(rec.side.is_some() && rec.clients.is_some());
    }
    assert_eq!((eml.version.as_str(), library::record(&dirs, &eml).unwrap().clients), ("1.2.0", Some(Clients::Mod)));
    assert_eq!(library::record(&dirs, &ge).unwrap().clients, Some(Clients::None));
    assert!(library::path_of(&dirs, &st).unwrap().join("minimap.dll").is_file());
    // the Library lists them with their manifest
    let listed = library::list(&dirs, None).unwrap();
    assert_eq!(listed.iter().filter(|e| e.kind.is_engine()).count(), 6);
    assert_eq!(listed.iter().find(|e| e.kind == ModKind::Eml).unwrap().versions[0].manifest.as_ref().unwrap()["name"], "Bigger Stacks");

    // a profile with one of each, the ShroudForge package on, the SET mod on
    let mut p = profile::create(&dirs, "All engines", None).unwrap();
    p.mods.eml = vec![pm(&eml, true)];
    p.mods.shroudforge = vec![pm(&sf, true)];
    p.mods.set = vec![pm(&set, true)];
    p.mods.greeneye = vec![pm(&ge, false)];
    p.mods.flame = vec![pm(&flame, true)];
    p.mods.shroudtopia = vec![pm(&st, true)];
    let p = profile::save(&dirs, &p).unwrap();
    assert_eq!(profile::list(&dirs).unwrap()[0].counts.shroudforge, 1);
    let game = game_with_z(&t.0);
    // a ShroudForge mod the player installed by hand stays theirs
    put(&game.path, "mods/handmade/mod.json", br#"{"id":"handmade","name":"Hand","version":"1.0.0"}"#);
    put(&game.path, "mods/handmade/extended.mod.json", br#"{"launcher":"SF","enabled":true}"#);
    put(&game.path, "mods/handmade/src/mod.lua", b"");
    profile::activate(&dirs, &p.id, &game).unwrap();
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    let z = |p: &Path| format!("Z:{}", p.display()).replace('/', "\\");
    assert_eq!(file.mods.eml[0].path, z(&library::path_of(&dirs, &eml).unwrap()));
    assert_eq!(file.mods.greeneye[0].enabled, false);
    assert_eq!(file.mods.flame[0].path, z(&library::path_of(&dirs, &flame).unwrap()));
    // ShroudForge and SET: the copies in their own folders, marked as Kiln's, switched as the profile says
    let sf_copy = game.path.join("mods/sfpack");
    let set_copy = game.path.join("shrouded_engine_tweaks/mods/flymode");
    assert_eq!(file.mods.shroudforge[0].path, z(&sf_copy));
    assert_eq!(file.mods.set[0].path, z(&set_copy));
    assert_eq!(engine_mods::marker(&sf_copy).unwrap().hash, sf.hash);
    assert!(set_copy.join("init.lua").is_file());
    assert!(kiln_manage::shroudforge::read_mod(&sf_copy).unwrap().enabled, "the profile switched it on");
    assert!(engine_mods::marker(&game.path.join("mods/handmade")).is_none());
    // the file lists the engine kinds only when they have entries
    let text = std::fs::read_to_string(game.path.join("kiln/profile.json")).unwrap();
    assert!(text.contains("\"shroudtopia\"") && !text.contains("\"enshwick\""));
    assert_ne!(file.fingerprint, None);

    // settings: ShroudForge on the placed copy, kept across a new activation
    let cfg = dirs.profile(&p.id).join("config");
    let s = engine_mods::settings(ModKind::ShroudForge, "sfpack", Some(&game.path), &cfg, None).unwrap();
    assert_eq!((s.format.as_str(), &s.values["speed"]), ("extended.mod.json", &serde_json::json!(3)));
    engine_mods::set_setting(ModKind::ShroudForge, "sfpack", "speed", &serde_json::json!(7), Some(&game.path), &cfg, None).unwrap();
    // Shroudtopia: shroudtopia.json in the profile's config folder, types from the values
    let st_folder = library::path_of(&dirs, &st).unwrap();
    let s = engine_mods::set_setting(ModKind::Shroudtopia, "minimap", "active", &serde_json::json!(false), None, &cfg, Some(&st_folder)).unwrap();
    assert_eq!(s.values["active"], false);
    assert!(engine_mods::set_setting(ModKind::Shroudtopia, "minimap", "active", &serde_json::json!("no"), None, &cfg, Some(&st_folder)).is_err());

    // switch SET off and drop ShroudForge from the profile: SET's copy goes, SF's copy is switched off, settings stay
    let mut p2 = profile::get(&dirs, &p.id).unwrap();
    p2.mods.set[0].enabled = false;
    p2.mods.shroudforge.clear();
    let p2 = profile::save(&dirs, &p2).unwrap();
    profile::activate(&dirs, &p2.id, &game).unwrap();
    assert!(!set_copy.exists());
    let m = kiln_manage::shroudforge::read_mod(&sf_copy).unwrap();
    assert!(!m.enabled);
    assert_eq!(m.settings.iter().find(|s| s.key == "speed").unwrap().value, 7);
    assert!(kiln_manage::shroudforge::read_mod(&game.path.join("mods/handmade")).unwrap().enabled, "never touched");
}

#[test]
fn eml_restore_puts_the_vanilla_kfc_back() {
    let t = tmp("eml-restore");
    put(&t.0, "enshrouded.kfc", b"patched");
    put(&t.0, "enshrouded.kfc.bak", b"vanilla");
    let b = engine_mods::eml_backup(&t.0);
    assert_eq!(b.len(), 1);
    assert!(b[0].patched);
    assert_eq!(engine_mods::eml_restore_vanilla(&t.0).unwrap().len(), 1);
    assert_eq!(std::fs::read(t.0.join("enshrouded.kfc")).unwrap(), b"vanilla");
    assert!(!engine_mods::eml_backup(&t.0)[0].patched);
    assert!(engine_mods::eml_restore_vanilla(&t.0).unwrap().is_empty());
}

#[test]
fn status_reads_each_plugins_state_file() {
    let t = tmp("status");
    put(&t.0, "kiln/state/eml.json", br#"{"phase":"done","mods":[{"id":"stacks","state":"loaded"}]}"#);
    let st = engine_mods::status(&t.0);
    let eml = st.iter().find(|s| s.kind == ModKind::Eml).unwrap();
    assert_eq!(eml.plugin, "kiln-eml");
    assert_eq!(eml.state.as_ref().unwrap()["phase"], "done");
    assert!(st.iter().find(|s| s.kind == ModKind::Flame).unwrap().state.is_none());
}

#[test]
fn modpacks_carry_engine_mods_players_need_with_their_kdh1() {
    let t = tmp("modpack");
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    // an EML data mod (players need it), a Greeneye client mod and a SET mod (players don't), a Flame mod (both)
    put(&server, "kiln/eml/mods/stacks/mod.json", br#"{"id":"stacks","name":"Bigger Stacks","version":"1.2.0","capabilities":["patch"]}"#);
    put(&server, "kiln/eml/mods/stacks/src/mod.lua", b"return {}");
    put(&server, "greeneye/worstmap/mod.json", br#"{"id":"worstmap","name":"Map","version":"1.0.0","dll":"map.dll"}"#);
    put(&server, "greeneye/worstmap/map.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    put(&server, "shrouded_engine_tweaks/mods/flymode/init.lua", b"print('x')");
    put(&server, "mods/creative/mod.json", br#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#);
    put(&server, "mods/creative/flame.json", br#"{"dll":"creative.dll"}"#);
    put(&server, "mods/creative/creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    let need = modpack::client_mods(&server).unwrap();
    assert_eq!(need.iter().map(|m| (m.kind, m.id.as_str())).collect::<Vec<_>>(), [(ModKind::Eml, "stacks"), (ModKind::Flame, "creative")]);
    let req = modpack::required(&server).unwrap();
    assert_eq!(req[0].kind, Some(ModKind::Eml));
    assert_eq!(req[0].hash, hash::dirhash(&server.join("kiln/eml/mods/stacks")).unwrap());
    let built = modpack::build(&server, &t.0.join("out"), "Isles", "1", &mut NoProgress).unwrap();
    assert_eq!(built.manifest.mods.iter().map(|m| m.path.as_str()).collect::<Vec<_>>(), ["eml/stacks", "flame/creative"]);
    // a player installs it: the library takes both kinds with the same kdh1, the profile lists them
    let dirs = DataDirs::at(t.0.join("data"));
    let game = game_with_z(&t.0);
    let res = modpack::install(&dirs, &game, &modpack::ModpackInstallRequest {
        source: modpack::ModpackSource::Path { path: built.file.clone(), sha256: Some(built.sha256.clone()) },
        consent: true, allow_http: false, activate: Some(false),
    }, &mut NoProgress).unwrap();
    assert_eq!(res.profile.mods.eml[0].hash, req[0].hash);
    assert_eq!(res.profile.mods.flame.len(), 1);
}

/// Lab helper (not a unit test): imports every mod under $KILN_WIRE_SRC into the library of $KILN_WIRE_DATA, makes the
/// profile "wire lab" with all of them, and activates it on the lab server folder $KILN_WIRE_LAB (Z: paths through the
/// lab's Wine prefix $KILN_WIRE_PFX). Servers keep the legacy layout in production; kiln-core reads a profile.json on
/// either side, which is what the lab checks. Run: cargo test -p kiln-manage --features full --test engine_mods -- --ignored lab_profile
#[test]
#[ignore]
fn lab_profile() {
    let env = |k: &str| PathBuf::from(std::env::var(k).unwrap_or_else(|_| panic!("set {k}")));
    let (src, data, lab, pfx) = (env("KILN_WIRE_SRC"), env("KILN_WIRE_DATA"), env("KILN_WIRE_LAB"), env("KILN_WIRE_PFX"));
    let dirs = DataDirs::at(data);
    let mut paths: Vec<PathBuf> = std::fs::read_dir(&src).unwrap().flatten().map(|e| e.path()).collect();
    paths.sort();
    let r = library::import(&dirs, &paths, &mut NoProgress).unwrap();
    println!("imported {:?}\nexisting {:?}\nskipped {:?}", r.imported, r.existing, r.skipped);
    let id = profile::list(&dirs).unwrap().into_iter().find(|p| p.name == "wire lab").map(|p| p.id);
    let mut p = match id {
        Some(id) => profile::get(&dirs, &id).unwrap(),
        None => profile::create(&dirs, "wire lab", None).unwrap(),
    };
    for x in r.imported.iter().chain(&r.existing) {
        let l = p.mods.list_mut(x.kind);
        l.retain(|m| m.id != x.id);
        l.push(pm(x, true));
    }
    let p = profile::save(&dirs, &p).unwrap();
    let game = GameInstall {
        id: "wv-wire".into(), kind: GameKind::Client, path: lab.clone(), exe: "enshrouded_server.exe".into(),
        app_id: kiln_manage::SERVER_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into(),
    };
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    println!("activated {a:?}");
    println!("{}", std::fs::read_to_string(lab.join("kiln/profile.json")).unwrap());
}

#[cfg(feature = "server")]
#[test]
fn server_sync_puts_engine_mods_in_each_engines_folder_and_switches_them() {
    use kiln_manage::server::{control, instance::ServerInstance};
    let t = tmp("server-sync");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    put(&server, "mods/handmade/flame.json", br#"{"dll":"x.dll"}"#); // the owner's own Flame mod
    let inst: ServerInstance = serde_json::from_value(serde_json::json!({
        "id": "t", "name": "t", "kind": "local", "dir": server, "port": 27140, "queryPort": 27140, "wine": null,
        "policy": "any", "source": "launcher", "remote": null })).unwrap();
    let want: Vec<LibraryRef> = [(ModKind::Eml, "stacks"), (ModKind::Flame, "creative"), (ModKind::Set, "flymode"), (ModKind::Greeneye, "worstmap")]
        .iter().map(|(k, id)| refs(&r, *k, id)).collect();
    let listed = control::sync(&dirs, &inst, &want, &mut NoProgress).unwrap();
    for (k, rel) in [(ModKind::Eml, "kiln/eml/mods/stacks"), (ModKind::Flame, "mods/creative"), (ModKind::Set, "shrouded_engine_tweaks/mods/flymode"),
                     (ModKind::Greeneye, "greeneye/worstmap")] {
        assert!(server.join(rel).join(".kiln-source.json").is_file(), "{rel}");
        assert!(listed.iter().any(|m| m.kind == Some(k) && m.source.is_some() && m.enabled), "{k:?} in {listed:?}");
    }
    assert!(listed.iter().any(|m| m.kind == Some(ModKind::Flame) && m.folder == "handmade" && m.source.is_none()));
    // off: out of the engine's folder; on: back
    control::set_enabled(&inst, "creative", false).unwrap();
    assert!(!server.join("mods/creative").exists() && server.join("kiln/disabled/flame/creative").is_dir());
    assert!(control::mods(&inst).unwrap().iter().any(|m| m.folder == "creative" && !m.enabled));
    control::set_enabled(&inst, "creative", true).unwrap();
    assert!(server.join("mods/creative/flame.json").is_file());
    // dropped from the list: Kiln's copy goes, the owner's stays
    control::sync(&dirs, &inst, &want[..1], &mut NoProgress).unwrap();
    assert!(!server.join("mods/creative").exists() && !server.join("greeneye/worstmap").exists());
    assert!(server.join("mods/handmade").is_dir() && server.join("kiln/eml/mods/stacks").is_dir());
}

#[test]
fn shroudlink_mod_dlls_are_detected_by_their_export() {
    let t = tmp("shroudlink");
    put(&t.0, "chestsort/chestsort.dll", &build_test_dll(&["shroudlink_mod_v2"], b""));
    let d = library::detect(&t.0.join("chestsort/chestsort.dll")).unwrap();
    assert_eq!((d.kind, d.engine), (library::DetectKind::Shroudlink, Some(kiln_manage::engines::Engine::Shroudlink)));
    assert!(!d.kind.importable(), "reserved: no importer yet");
}

#[test]
fn required_mods_read_engine_kinds_and_tolerate_unknown_ones() {
    let r: Vec<kiln_manage::state::RequiredMod> = serde_json::from_str(
        r#"[{"id":"a","version":"1","hash":"kdh1:x","kind":"eml"},{"id":"b","version":"1","hash":"kdh1:y","kind":"from-the-future"},{"id":"c","version":"1","hash":"kdh1:z"}]"#,
    ).unwrap();
    assert_eq!(r.iter().map(|m| m.kind).collect::<Vec<_>>(), [Some(ModKind::Eml), None, None]);
}

/// A drop on the launcher window (quick::add): an EML mod and a ShroudForge package go into "My mods" under their kind
/// and the profile is applied (the SF package placed in ShroudForge's mods folder).
#[test]
fn a_dropped_engine_mod_lands_in_the_active_profile() {
    let t = tmp("drop");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = game_with_z(&t.0);
    let src = sources(&t.0);
    let rep = kiln_manage::quick::add_with(&dirs, &game, &src[..2], false, &mut NoProgress).unwrap();
    assert!(rep.applied, "{rep:?}");
    let p = profile::get(&dirs, rep.profile_id.as_deref().unwrap()).unwrap();
    assert_eq!((p.mods.eml.len(), p.mods.shroudforge.len()), (1, 1));
    assert!(engine_mods::marker(&game.path.join("mods/sfpack")).is_some());
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    assert_eq!((file.mods.eml.len(), file.mods.shroudforge.len()), (1, 1));
}
