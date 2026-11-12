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
use kiln_manage::ErrorCode;
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
    put(&sf, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"targets":["client","server"],
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

/// A failed recovery copy leaves that mod running, but must not prevent another profile or Play.
#[test]
fn linked_set_recovery_does_not_block_switching_profiles_or_play() {
    use kiln_manage::launch::{self, LaunchMode, LaunchRequest};
    for listed_off in [false, true] {
        let t = tmp(&format!("linked-recovery-{listed_off}"));
        let dirs = DataDirs::at(t.0.join("data"));
        let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
        let set = refs(&r, ModKind::Set, "flymode");
        let ge = refs(&r, ModKind::Greeneye, "worstmap");
        let game = game_with_z(&t.0);
        let mut first = profile::create(&dirs, "First", None).unwrap();
        first.mods.set.push(pm(&set, true));
        let first = profile::save(&dirs, &first).unwrap();
        profile::activate(&dirs, &first.id, &game).unwrap();
        let dst = game.path.join("shrouded_engine_tweaks/mods/flymode");
        put(&dst, "settings.json", b"player settings");
        let outside = t.0.join("outside");
        put(&outside, "settings.json", b"older recovery");
        let kept = game.path.join("kiln/kept-mod-files/set/flymode");
        std::fs::create_dir_all(kept.parent().unwrap()).unwrap();
        std::os::unix::fs::symlink(&outside, &kept).unwrap();
        let mut second = profile::create(&dirs, "Second", None).unwrap();
        if listed_off { second.mods.set.push(pm(&set, false)); }
        second.mods.greeneye.push(pm(&ge, true));
        let second = profile::save(&dirs, &second).unwrap();
        let result = profile::activate(&dirs, &second.id, &game);
        assert!(result.is_ok(), "one refused recovery copy must not block activation: {result:?}");
        let a = result.unwrap();
        assert_eq!(a.profile_id, second.id);
        assert!(a.retained.iter().any(|r| r.path == dst && r.may_run && r.reason.contains("symlink")), "{a:?}");
        assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), b"player settings");
        assert_eq!(std::fs::read(outside.join("settings.json")).unwrap(), b"older recovery");
        assert_eq!(std::fs::read_dir(&outside).unwrap().count(), 1);
        assert!(game.path.join("greeneye/worstmap/map.dll").is_file(), "the next mod is placed");
        let opened = std::cell::Cell::new(false);
        let request = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
        let played = launch::play_with(&dirs, &game, &request, Some(&[]), &|_| { opened.set(true); Ok(()) }).unwrap();
        assert!(opened.get());
        assert!(played.activation.unwrap().retained.iter().any(|r| r.path == dst && r.may_run));
    }
}

#[test]
fn play_does_not_open_steam_when_the_refreshed_profile_cannot_be_written() {
    use kiln_manage::launch::{self, LaunchMode, LaunchRequest};
    let t = tmp("play-write-fails");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let game = game_with_z(&t.0);
    let mut p = profile::create(&dirs, "Before play", None).unwrap();
    p.mods.set.push(pm(&refs(&r, ModKind::Set, "flymode"), true));
    let mut p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let before = std::fs::read(game.path.join("kiln/profile.json")).unwrap();
    p.mods.set.clear();
    profile::save(&dirs, &p).unwrap();
    // Staging the refreshed JSON fails after placement, without touching the current file.
    std::fs::create_dir(game.path.join("kiln/profile.json.tmp")).unwrap();
    let opened = std::cell::Cell::new(false);
    let request = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
    let e = launch::play_with(&dirs, &game, &request, Some(&[]), &|_| { opened.set(true); Ok(()) }).unwrap_err();
    assert!(!opened.get(), "a failed refresh must never start Steam");
    assert!(e.message.contains("cannot be applied") && e.message.contains("profile.json.tmp"), "{e:?}");
    assert_eq!(std::fs::read(game.path.join("kiln/profile.json")).unwrap(), before);
}

#[test]
fn failed_play_returns_the_refresh_activation_and_the_original_error_detail() {
    use kiln_manage::launch::{self, LaunchMode, LaunchRequest};
    for detail in [serde_json::json!({"step":"opener"}), serde_json::json!("opener detail")] {
        let t = tmp(if detail.is_object() { "play-error-object" } else { "play-error-value" });
        let dirs = DataDirs::at(t.0.join("data"));
        let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
        let game = game_with_z(&t.0);
        let mut p = profile::create(&dirs, "Before play", None).unwrap();
        p.mods.set.push(pm(&refs(&r, ModKind::Set, "flymode"), true));
        let mut p = profile::save(&dirs, &p).unwrap();
        let first = profile::activate(&dirs, &p.id, &game).unwrap();
        put(&game.path, "shrouded_engine_tweaks/mods/flymode/init.lua", b"player code");
        p.mods.set.clear();
        profile::save(&dirs, &p).unwrap();
        let opened = std::cell::Cell::new(false);
        let request = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
        let e = launch::play_with(&dirs, &game, &request, Some(&[]), &|_| {
            opened.set(true);
            Err(kiln_manage::Error::invalid("injected opener failure").with_detail(detail.clone()))
        }).unwrap_err();
        assert!(opened.get());
        assert_eq!(e.message, "injected opener failure");
        let value = e.detail.unwrap();
        if detail.is_object() { assert_eq!(value["step"], "opener"); } else { assert_eq!(value["detail"], detail); }
        let a: profile::Activation = serde_json::from_value(value["activation"].clone()).unwrap();
        assert!(a.generation > first.generation);
        assert_eq!(a.generation, profile::read_game_profile(&game.path).unwrap().unwrap().generation);
        assert!(a.retained.iter().any(|r| r.path == game.path.join("shrouded_engine_tweaks/mods/flymode") && r.may_run));
        assert_eq!(std::fs::read(game.path.join("shrouded_engine_tweaks/mods/flymode/init.lua")).unwrap(), b"player code");
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
    put(&game.path, "mods/handmade/extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":true}"#);
    put(&game.path, "mods/handmade/src/mod.lua", b"");
    profile::activate(&dirs, &p.id, &game).unwrap();
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    let z = |p: &Path| format!("Z:{}", p.display()).replace('/', "\\");
    assert_eq!(file.mods.eml[0].path, z(&library::path_of(&dirs, &eml).unwrap()));
    assert_eq!(file.mods.greeneye[0].enabled, false);
    // a Flame mod reads its own files from <game>\mods\<id>\: it runs from Kiln's copy there (flame_creative_mode.rs)
    assert_eq!(file.mods.flame[0].path, z(&game.path.join("mods/creative")));
    assert_eq!(engine_mods::marker(&game.path.join("mods/creative")).unwrap().hash, flame.hash);
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
    let listed: serde_json::Value = serde_json::from_str(&text).unwrap();
    assert!(listed["mods"].get("shroudtopia").is_some() && listed["mods"].get("enshwick").is_none(), "{}", listed["mods"]);
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
fn direct_restore_never_overwrites_shared_archive_data_or_another_loaders_state() {
    let t = tmp("eml-restore");
    let files: &[(&str, &[u8])] = &[
        ("enshrouded.kfc", b"another loader's patched header"),
        ("enshrouded.kfc.bak", b"shared baseline header"),
        ("enshrouded.kfc_resources", b"another loader's patched resources"),
        ("enshrouded.kfc_resources.bak", b"shared baseline resources"),
        ("enshrouded_010.dat", b"published content"),
        ("shroudforge/state.json", br#"{"assets":{"status":"applied"}}"#),
        ("kiln/eml/ownership/enshrouded.json", b"unverified receipt"),
        ("player-data", b"keep"),
    ];
    for (name, bytes) in files { put(&t.0, name, bytes); }
    let b = engine_mods::eml_backup(&t.0).unwrap();
    assert_eq!(b.len(), 1);
    assert!(b[0].patched, "a difference does not prove who wrote it");
    let error = engine_mods::eml_restore_vanilla(&t.0).unwrap_err();
    assert_eq!(error.code, ErrorCode::Refused);
    assert!(error.message.contains("ownership") && error.message.contains("Kiln"));
    for (name, bytes) in files {
        assert_eq!(std::fs::read(t.0.join(name)).unwrap(), *bytes, "{name}");
    }
    assert!(engine_mods::eml_backup(&t.0).unwrap()[0].patched);
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
    assert!(d.kind.importable(), "kiln-shroudlink's importer: {}", d.reason);
    assert!(d.reason.contains("Library importer: kiln-shroudlink"), "{}", d.reason);
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

#[test]
fn placement_never_trusts_payload_markers_or_switches_player_packages() {
    let t = tmp("spoofed-marker");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "ownership", None).unwrap();
    let game = t.0.join("game");
    let sf = refs(&r, ModKind::ShroudForge, "sfpack");
    let set = refs(&r, ModKind::Set, "flymode");
    let sf_copy = game.join("mods/sfpack");
    kiln_manage::util::copy_tree(&t.0.join("src/sfpack"), &sf_copy).unwrap();
    let ext = sf_copy.join("extended.mod.json");
    let original = std::fs::read(&ext).unwrap();
    p.mods.shroudforge.push(pm(&sf, true));
    let set_copy = game.join("shrouded_engine_tweaks/mods/flymode");
    put(&set_copy, "player.txt", b"player data");
    let forged = engine_mods::Marker { format: 1, kind: ModKind::Set, id: set.id.clone(), version: set.version.clone(), hash: set.hash.clone() };
    kiln_manage::util::write_json_atomic(&set_copy.join(engine_mods::MARKER), &forged).unwrap();
    // Neither a matching marker nor a listed disabled SET entry confers ownership.
    p.mods.set.push(pm(&set, false));
    engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(std::fs::read(&ext).unwrap(), original);
    assert_eq!(std::fs::read(set_copy.join("player.txt")).unwrap(), b"player data");
    p.mods.set.clear();
    engine_mods::place(&dirs, &p, &game).unwrap();
    assert!(set_copy.join("player.txt").is_file());
}

#[test]
fn sf_flight_switches_remain_independent_through_archive_activation_and_update() {
    use std::io::Write;
    let t = tmp("sf-flight-settings");
    let dirs = DataDirs::at(t.0.join("data"));
    // Synthetic package, not a copy of a third-party mod. The two controls must not be conflated.
    let import = |version: &str| {
        let archive = t.0.join(format!("flight-{version}.zip"));
        let mut zip = zip::ZipWriter::new(std::fs::File::create(&archive).unwrap());
        for (path, text) in [
            ("mod.json", format!(r#"{{"id":"flight-fixture","name":"Flight fixture","version":"{version}","capabilities":["runtime"]}}"#)),
            ("extended.mod.json", r#"{"schemaVersion":1,"launcher":"SF","enabled":false,"targets":["client","server"],"settings":{"unlimitedFlying":{"value":true},"unlimitedGliderStamina":{"value":true}}}"#.into()),
            ("src/mod.lua", "return {}".into()),
        ] {
            zip.start_file(format!("bundle/flight/{path}"), zip::write::SimpleFileOptions::default()).unwrap();
            zip.write_all(text.as_bytes()).unwrap();
        }
        zip.finish().unwrap();
        refs(&library::import(&dirs, &[archive], &mut NoProgress).unwrap(), ModKind::ShroudForge, "flight-fixture")
    };
    let first = import("1.0.0");
    let source = library::path_of(&dirs, &first).unwrap();
    let original = std::fs::read(source.join("extended.mod.json")).unwrap();
    let mut p = profile::create(&dirs, "Flight settings", None).unwrap();
    p.mods.shroudforge.push(pm(&first, true));
    p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    put(&game.path, kiln_manage::shroudforge::CONFIG_FILE, br#"{"paths":{"mods":"custom\\mods"}}"#);
    let dst = game.path.join("custom/mods/flight-fixture");
    let cfg = dirs.profile(&p.id).join("config");
    let values = || engine_mods::settings(ModKind::ShroudForge, "flight-fixture", Some(&game.path), &cfg, Some(&source)).unwrap().values;
    profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(values(), serde_json::json!({"unlimitedFlying":true,"unlimitedGliderStamina":true}));
    assert!(kiln_manage::shroudforge::read_mod(&dst).unwrap().enabled);
    engine_mods::set_setting(ModKind::ShroudForge, "flight-fixture", "unlimitedFlying", &false.into(), Some(&game.path), &cfg, None).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(values(), serde_json::json!({"unlimitedFlying":false,"unlimitedGliderStamina":true}));
    p.mods.shroudforge[0] = pm(&import("2.0.0"), true);
    profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(kiln_manage::shroudforge::read_mod(&dst).unwrap().version, "2.0.0");
    assert_eq!(values(), serde_json::json!({"unlimitedFlying":false,"unlimitedGliderStamina":true}));
    engine_mods::set_setting(ModKind::ShroudForge, "flight-fixture", "unlimitedFlying", &true.into(), Some(&game.path), &cfg, None).unwrap();
    engine_mods::set_setting(ModKind::ShroudForge, "flight-fixture", "unlimitedGliderStamina", &false.into(), Some(&game.path), &cfg, None).unwrap();
    assert_eq!(values(), serde_json::json!({"unlimitedFlying":true,"unlimitedGliderStamina":false}));
    assert_eq!(std::fs::read(source.join("extended.mod.json")).unwrap(), original, "settings writes must target the placed copy, not the Library");
}

#[test]
fn sf_state_with_its_shortcut_source_reaches_engine_status_and_the_report() {
    // kiln-shroudforge writes "keys" (dev.24): where ShroudForge's mod shortcuts get their key state this start. It is
    // passed on as written; the report's engine line still reads mode and reason, and no mod row comes from it.
    let t = tmp("sf-keys");
    let game = &t.0;
    put(game, "kiln/state/shroudforge.json", br#"{"format":1,"side":"client","mode":"hosted","reason":"","runtime":"running",
        "source":"bundled","version":"1.7.9","menu":"kiln","menuKey":"SDL:66","engineHooks":"self",
        "keys":{"source":"kiln","reason":"Kiln passes the game window's key, mouse button and focus records to the runtime (host key feed v1); this is not a check that a mod's shortcut fires"}}"#);
    let sf = engine_mods::status(game).into_iter().find(|s| s.kind == ModKind::ShroudForge).unwrap();
    assert!(sf.error.is_none(), "{:?}", sf.error);
    let state = sf.state.unwrap();
    assert_eq!(state["keys"]["source"], "kiln");
    assert_eq!(kiln_manage::report::engine_headline(&state).as_deref(), Some("hosted"));
    assert!(kiln_manage::report::engine_mod_lines(&state).is_empty());
}

#[test]
fn sf_effects_reach_engine_status_without_presenting_old_or_invalid_reports_as_current() {
    let t = tmp("sf-effects");
    let game = &t.0;
    let read = || engine_mods::status(game).into_iter().find(|s| s.kind == ModKind::ShroudForge).unwrap().effects.unwrap();
    assert_eq!(read().state, "unavailable");
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
    let state = |running: bool, updated: u64| serde_json::json!({"runtime": {
        "running":running,"updatedAt":updated,"active":["flight-fixture"],
        "effects":{"flight-fixture":{"state":"no-target","detail":"No live player entity is available","updatedAt":1}}
    }});
    let write = |doc: serde_json::Value| put(game, "shroudforge/state.json", &serde_json::to_vec(&doc).unwrap());
    write(state(true, now));
    let fresh = read();
    assert_eq!(fresh.state, "current");
    assert_eq!(fresh.mods.len(), 1);
    assert_eq!(fresh.mods[0].id, "flight-fixture");
    assert_eq!(fresh.mods[0].state, "no-target");
    assert_eq!(fresh.mods[0].detail, "No live player entity is available");
    let mut unsupported = state(true, now);
    unsupported["runtime"]["effects"]["flight-fixture"]["state"] = "waiting".into();
    unsupported["runtime"]["effects"]["flight-fixture"]["detail"] = "Upward tilt requires pitch-speed input controls. Target-angle controls are not supported".into();
    write(unsupported);
    assert_eq!(read().mods[0].state, "waiting");
    assert!(read().mods[0].detail.contains("Target-angle"));
    // An unchanged effect is still valid when the runtime heartbeat is current.
    for (running, updated, expected) in [(true, now - 120, "stale"), (false, now, "stopped"), (true, now + 120, "stale")] {
        write(state(running, updated));
        let old = read();
        assert_eq!(old.state, expected);
        assert!(old.mods.is_empty());
        assert!(old.reason.is_some());
    }
    put(game, "shroudforge/state.json", b"not JSON");
    assert_eq!(read().state, "invalid");
    write(serde_json::json!({"runtime":{"running":true,"effects":{}}}));
    assert_eq!(read().state, "invalid", "a missing heartbeat cannot prove a live mod effect");
    let mut bad = state(true, now);
    bad["runtime"]["effects"]["flight-fixture"]["detail"] = 7.into();
    write(bad);
    assert_eq!(read().state, "invalid");
    let mut failed = state(true, now);
    failed["runtime"]["effects"]["flight-fixture"]["state"] = "write-failed".into();
    write(failed);
    assert_eq!(read().mods[0].state, "write-failed");
    let mut disabled = state(true, now);
    disabled["runtime"]["active"] = serde_json::json!([]);
    disabled["runtime"]["effects"]["flight-fixture"]["state"] = "write-confirmed".into();
    write(disabled);
    assert!(read().mods.is_empty(), "an inactive mod's old write must not look current");
    let mut error = state(true, now);
    error["runtime"]["errors"] = serde_json::json!({"flight-fixture":"Lua callback failed"});
    write(error);
    assert_eq!(read().mods[0].state, "error");
    assert_eq!(read().mods[0].detail, "Lua callback failed");
    put(game, "kiln/state/shroudforge.json", br#"{"mode":"hosted","runtime":"stopped"}"#);
    assert_eq!(read().state, "stopped", "an adapter that just stopped invalidates even a recent SF heartbeat");
    std::fs::remove_file(game.join("kiln/state/shroudforge.json")).unwrap();
    let wire = serde_json::to_value(engine_mods::status(game)).unwrap();
    let sf = wire.as_array().unwrap().iter().find(|s| s["kind"] == "shroudforge").unwrap();
    assert_eq!(sf["effects"]["updatedAt"], now);
    assert_eq!(sf["effects"]["mods"][0]["detail"], "Lua callback failed");
    put(game, kiln_manage::shroudforge::CONFIG_FILE, br#"{"paths":{"state":"custom\\state"}}"#);
    put(game, "custom/state/state.json", &serde_json::to_vec(&state(true, now)).unwrap());
    assert_eq!(read().state, "current");
    assert!(read().file.ends_with("custom/state/state.json"));
    let win_state = format!("Z:{}", game.join("custom/state").display()).replace('/', "\\");
    put(game, kiln_manage::shroudforge::CONFIG_FILE, &serde_json::to_vec(&serde_json::json!({"paths":{"state":win_state}})).unwrap());
    assert_eq!(read().state, "current", "configured Windows state paths must resolve under Proton too");
    // Do not read through arbitrary links or reinterpret a malformed config as the default path.
    std::fs::remove_file(game.join("custom/state/state.json")).unwrap();
    std::os::unix::fs::symlink(game.join("shroudforge/state.json"), game.join("custom/state/state.json")).unwrap();
    assert_eq!(read().state, "invalid");
    put(game, kiln_manage::shroudforge::CONFIG_FILE, b"not JSON");
    assert_eq!(read().state, "invalid");
    std::fs::remove_file(game.join(kiln_manage::shroudforge::CONFIG_FILE)).unwrap();
    std::fs::remove_file(game.join("shroudforge/state.json")).unwrap();
    std::fs::create_dir(game.join("shroudforge/state.json")).unwrap();
    assert_eq!(read().state, "invalid", "do not read non-regular state files");
    std::fs::remove_dir(game.join("shroudforge/state.json")).unwrap();
    put(game, "shroudforge/state.json", &vec![b' '; 2 * 1024 * 1024 + 1]);
    assert!(read().reason.unwrap().contains("2 MiB"));
}

#[test]
fn placement_preserves_settings_across_versions_and_stages_before_replacing() {
    let t = tmp("version-settings");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = sources(&t.0)[1].clone();
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "versions", None).unwrap();
    p.mods.shroudforge.push(pm(&refs(&r, ModKind::ShroudForge, "sfpack"), true));
    let game = t.0.join("game");
    engine_mods::place(&dirs, &p, &game).unwrap();
    let dst = game.join("mods/sfpack");
    let cfg = t.0.join("config");
    engine_mods::set_setting(ModKind::ShroudForge, "sfpack", "speed", &serde_json::json!(7), Some(&game), &cfg, None).unwrap();
    put(&src, "mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"2.0.0"}"#);
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    p.mods.shroudforge[0] = pm(&refs(&r, ModKind::ShroudForge, "sfpack"), true);
    // An old predictable staging path is not ours to delete.
    put(&game, "mods/.kiln-place-sfpack/player.txt", b"keep");
    engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(kiln_manage::shroudforge::read_mod(&dst).unwrap().settings[0].value, 7);
    assert!(game.join("mods/.kiln-place-sfpack/player.txt").is_file());
    put(&src, "mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"3.0.0"}"#);
    put(&src, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"settings":{"speed":{"value":1,"min":0,"max":2}}}"#);
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    p.mods.shroudforge[0] = pm(&refs(&r, ModKind::ShroudForge, "sfpack"), true);
    // An old value the new version refuses does not hold the update back (nor every other mod of the profile): the
    // new version starts with its own default, and the result names the value that was not carried.
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    let m = kiln_manage::shroudforge::read_mod(&dst).unwrap();
    assert_eq!(m.version, "3.0.0");
    assert_eq!(m.settings[0].value, 1);
    let warnings = serde_json::to_value(&rep).unwrap()["warnings"].clone();
    assert!(warnings.as_array().into_iter().flatten().filter_map(|w| w.as_str())
        .any(|w| w.contains("shroudforge:sfpack") && w.contains("speed") && w.contains('7')), "{rep:?}");
}

#[test]
fn placement_refuses_modified_owned_trees_and_symlinked_roots() {
    let t = tmp("placement-links");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "links", None).unwrap();
    p.mods.set.push(pm(&refs(&r, ModKind::Set, "flymode"), true));
    let game = t.0.join("game");
    engine_mods::place(&dirs, &p, &game).unwrap();
    let dst = game.join("shrouded_engine_tweaks/mods/flymode");
    // the player edited a file of the library copy: that copy is no longer Kiln's to remove. It stays, is reported
    // as possibly still running, and does not stop the rest of the activation.
    put(&dst, "init.lua", b"print('edited')");
    p.mods.set.clear();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.retained.len(), 1, "{rep:?}");
    assert!(rep.retained[0].path == dst && rep.retained[0].may_run && rep.retained[0].reason.contains("managed copy changed"), "{rep:?}");
    assert!(rep.removed.is_empty(), "{rep:?}");
    assert_eq!(std::fs::read(dst.join("init.lua")).unwrap(), b"print('edited')");
    let linked_game = t.0.join("linked-game");
    std::fs::create_dir_all(&linked_game).unwrap();
    std::os::unix::fs::symlink(game.join("shrouded_engine_tweaks"), linked_game.join("shrouded_engine_tweaks")).unwrap();
    assert!(engine_mods::place(&dirs, &p, &linked_game).is_err());
    // Also refuse traversal inputs before constructing a destination.
    p.mods.set.push(ProfileMod { id: "../escape".into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "0".repeat(64)), enabled: false });
    assert!(engine_mods::place(&dirs, &p, &t.0.join("other-game")).is_err());
}

/// Fedora Atomic / Bazzite: `/home` is a link to `/var/home`. The data dir and the game folder are reached through it.
/// Links above the managed roots work; links inside the Library entry or the destination tree are still refused.
#[test]
fn placement_works_through_symlinked_system_ancestors_but_refuses_links_inside() {
    let t = tmp("system-ancestors");
    std::fs::create_dir_all(t.0.join("var/home/user")).unwrap();
    std::os::unix::fs::symlink("var/home", t.0.join("home")).unwrap();
    let dirs = DataDirs::at(t.0.join("home/user/.local/share/kiln"));
    let game = t.0.join("home/user/game");
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "atomic", None).unwrap();
    let sf = refs(&r, ModKind::ShroudForge, "sfpack");
    let set = refs(&r, ModKind::Set, "flymode");
    p.mods.shroudforge.push(pm(&sf, true));
    p.mods.set.push(pm(&set, true));
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed.len(), 2, "{rep:?}");
    assert!(game.join("shrouded_engine_tweaks/mods/flymode/init.lua").is_file());
    assert!(game.join("mods/sfpack/mod.json").is_file());
    // a second pass keeps them, and settings and the EML-style status reads work through the link too
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().kept.len(), 2);
    let cfg = t.0.join("home/user/config");
    let s = engine_mods::set_setting(ModKind::ShroudForge, "sfpack", "speed", &serde_json::json!(5), Some(&game), &cfg, None).unwrap();
    assert_eq!(s.values["speed"], 5);
    assert!(engine_mods::status(&game).iter().all(|s| s.error.is_none()));
    // removing the mod from the profile removes the placed SET copy through the link as well
    p.mods.set.clear();
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().removed.len(), 1);
    assert!(!game.join("shrouded_engine_tweaks/mods/flymode").exists());

    // A link INSIDE the destination tree is refused, even behind the linked ancestor.
    let elsewhere = t.0.join("home/user/elsewhere");
    std::fs::create_dir_all(&elsewhere).unwrap();
    std::fs::remove_dir_all(game.join("shrouded_engine_tweaks")).unwrap();
    std::os::unix::fs::symlink(&elsewhere, game.join("shrouded_engine_tweaks")).unwrap();
    p.mods.set.push(pm(&set, true));
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap_err().code, ErrorCode::Refused);
    assert!(std::fs::read_dir(&elsewhere).unwrap().next().is_none(), "nothing was written through the link");
    std::fs::remove_file(game.join("shrouded_engine_tweaks")).unwrap();

    // A link as the Library entry is refused too: the entry becomes a link to a copy of itself.
    let entry = library::path_of(&dirs, &set).unwrap();
    let copy = t.0.join("home/user/entry-copy");
    kiln_manage::util::copy_tree(&entry, &copy).unwrap();
    std::fs::remove_dir_all(&entry).unwrap();
    std::os::unix::fs::symlink(&copy, &entry).unwrap();
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap_err().code, ErrorCode::Refused);
    assert!(!game.join("shrouded_engine_tweaks/mods/flymode").exists());
}

#[test]
fn settings_refuse_undeclared_types_corrupt_documents_and_player_paths() {
    let t = tmp("settings-validation");
    let cfg = t.0.join("config");
    let file = engine_mods::shroudtopia_file(&cfg);
    put(&cfg, "shroudtopia/shroudtopia.json", br#"{"mods":{"map":{"zoom":2,"nested":{"active":true}}},"keep":3}"#);
    let before = std::fs::read(&file).unwrap();
    for (key, value) in [("unknown", serde_json::json!(true)), ("zoom", serde_json::json!(1.5)), ("nested", serde_json::json!({"new":false}))] {
        assert!(engine_mods::set_setting(ModKind::Shroudtopia, "map", key, &value, None, &cfg, None).is_err());
        assert_eq!(std::fs::read(&file).unwrap(), before);
    }
    std::fs::write(&file, b"{broken").unwrap();
    assert!(engine_mods::set_setting(ModKind::Shroudtopia, "map", "active", &serde_json::json!(false), None, &cfg, None).is_err());
    assert_eq!(std::fs::read(&file).unwrap(), b"{broken");
    for id in ["../map", "map\n"] { assert!(engine_mods::settings(ModKind::ShroudForge, id, Some(&t.0), &cfg, None).is_err()); }
    let own = t.0.join("mods/own");
    put(&own, "mod.json", br#"{"id":"own","name":"Own","version":"1.0.0"}"#);
    put(&own, "extended.mod.json", br#"{"settings":{"speed":1}}"#);
    assert!(engine_mods::set_setting(ModKind::ShroudForge, "own", "speed", &serde_json::json!(2), Some(&t.0), &cfg, None).is_err());
}

#[test]
fn engine_status_reports_bad_json_and_eml_restore_refuses_symlinks() {
    let t = tmp("status-errors");
    put(&t.0, "kiln/state/eml.json", b"{broken");
    let st = engine_mods::status(&t.0);
    assert!(st.iter().find(|s| s.kind == ModKind::Eml).unwrap().error.is_some());
    assert!(st.iter().find(|s| s.kind == ModKind::Flame).unwrap().error.is_none());
    put(&t.0, "enshrouded.kfc", b"patched");
    put(&t.0, "vanilla", b"vanilla");
    std::os::unix::fs::symlink(t.0.join("vanilla"), t.0.join("enshrouded.kfc.bak")).unwrap();
    assert!(engine_mods::eml_backup(&t.0).is_err());
    // eml_restore_vanilla refuses every folder (direct_restore_never_overwrites_shared_archive_data...): a check of it
    // here could not fail, whatever it did with the link.
    assert_eq!(std::fs::read(t.0.join("enshrouded.kfc")).unwrap(), b"patched");
}

#[test]
fn settings_use_manifest_name_before_first_boot_and_refuse_symlink_files() {
    let t = tmp("settings-name");
    let cfg = t.0.join("config");
    let folder = t.0.join("mod");
    put(&folder, "mod.json", br#"{"id":"map","name":"Pretty Map","shroudtopia":{"default":{"zoom":2}}}"#);
    let values = engine_mods::set_setting(ModKind::Shroudtopia, "map", "zoom", &serde_json::json!(3), None, &cfg, Some(&folder)).unwrap();
    assert_eq!(values.id, "Pretty Map");
    let file = engine_mods::shroudtopia_file(&cfg);
    let saved: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
    assert_eq!(saved["mods"]["Pretty Map"]["zoom"], 3);
    assert!(saved["mods"].get("map").is_none());
    std::fs::remove_file(&file).unwrap();
    put(&t.0, "player.json", br#"{"mods":{"map":{"active":true}}}"#);
    std::os::unix::fs::symlink(t.0.join("player.json"), &file).unwrap();
    assert!(engine_mods::set_setting(ModKind::Shroudtopia, "map", "active", &serde_json::json!(false), None, &cfg, None).is_err());
    assert_eq!(std::fs::read(t.0.join("player.json")).unwrap(), br#"{"mods":{"map":{"active":true}}}"#);
}

#[test]
fn concurrent_placement_is_serialized_and_modified_library_is_refused() {
    let t = tmp("placement-concurrent");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "concurrent", None).unwrap();
    p.mods.set.push(pm(&refs(&r, ModKind::Set, "flymode"), true));
    p.mods.shroudforge.push(pm(&refs(&r, ModKind::ShroudForge, "sfpack"), true));
    let game = t.0.join("game");
    std::thread::scope(|scope| {
        let threads: Vec<_> = (0..4).map(|_| scope.spawn(|| engine_mods::place(&dirs, &p, &game))).collect();
        for thread in threads { thread.join().unwrap().unwrap(); }
    });
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.kept.len(), 2);
    // A new destination must not receive a library entry whose contents no longer match its reference.
    let set = refs(&r, ModKind::Set, "flymode");
    put(&library::path_of(&dirs, &set).unwrap(), "init.lua", b"changed");
    assert!(engine_mods::place(&dirs, &p, &t.0.join("other-game")).is_err());
    assert!(!t.0.join("other-game/shrouded_engine_tweaks/mods/flymode").exists());
}

/// A ShroudForge package as its author ships it: mod.json `id` as given, an SF extension, Lua code.
fn sf_package(dir: &Path, id: &str) {
    put(dir, "mod.json", format!(r#"{{"id":"{id}","name":"Package {id}","version":"1.0.0"}}"#).as_bytes());
    put(dir, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"settings":{"speed":{"label":"Speed","control":"slider","min":1,"max":10,"value":3}}}"#);
    put(dir, "src/mod.lua", b"return {}");
}

#[test]
fn shroudforge_packages_whose_id_is_not_a_kiln_id_import_under_a_slug() {
    let t = tmp("sf-wide-id");
    let dirs = DataDirs::at(t.0.join("data"));
    let long = format!("Sf.Pack-{}", "X".repeat(62)); // upper case, 70 characters: ShroudForge's rule, not Kiln's
    assert_eq!(long.len(), 70);
    let a = t.0.join("src/HoardersHelper");
    let b = t.0.join("src/long");
    sf_package(&a, "HoardersHelper");
    sf_package(&b, &long);
    let bad = t.0.join("src/bad");
    sf_package(&bad, "has space");
    let r = library::import(&dirs, &[a.clone(), b.clone(), bad], &mut NoProgress).unwrap();
    assert_eq!(r.skipped.len(), 1, "{:?}", r.skipped);
    assert!(r.skipped[0].reason.contains("not a valid ShroudForge package id"), "{:?}", r.skipped);
    let ha = refs(&r, ModKind::ShroudForge, "hoardershelper");
    let slug = library::id_from_name(&long);
    let hb = refs(&r, ModKind::ShroudForge, &slug);
    // the files are byte-identical; the manifest's own id stays in the record
    assert_eq!(hash::dirhash(&library::path_of(&dirs, &ha).unwrap()).unwrap(), hash::dirhash(&a).unwrap());
    assert_eq!(library::record(&dirs, &ha).unwrap().manifest_id.as_deref(), Some("HoardersHelper"));
    assert_eq!(library::record(&dirs, &hb).unwrap().manifest_id.as_deref(), Some(long.as_str()));

    // placed as <sf mods>/<library id>/, switched and set by the Library id, read by ShroudForge's own id
    let game = t.0.join("game");
    let mut p = profile::create(&dirs, "wide", None).unwrap();
    p.mods.shroudforge = vec![pm(&ha, true), pm(&hb, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed.len(), 2, "{rep:?}");
    let da = game.join("mods/hoardershelper");
    assert_eq!(engine_mods::placed_folder(ModKind::ShroudForge, &game, &ha.id), Some(da.clone()));
    let m = kiln_manage::shroudforge::read_mod(&da).unwrap();
    assert_eq!((m.id.as_str(), m.enabled), ("HoardersHelper", true));
    assert!(game.join("mods").join(&slug).join("mod.json").is_file());
    let cfg = t.0.join("config");
    engine_mods::set_setting(ModKind::ShroudForge, &ha.id, "speed", &serde_json::json!(8), Some(&game), &cfg, None).unwrap();
    let s = engine_mods::settings(ModKind::ShroudForge, &ha.id, Some(&game), &cfg, None).unwrap();
    assert_eq!(s.values["speed"], 8);
    p.mods.shroudforge[0].enabled = false;
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.switched, vec!["shroudforge:hoardershelper".to_string()]);
    assert!(!kiln_manage::shroudforge::read_mod(&da).unwrap().enabled);
    assert_eq!(kiln_manage::shroudforge::read_mod(&da).unwrap().settings[0].value, 8, "settings kept");
}

#[test]
fn a_players_own_shroudforge_folder_with_the_same_package_id_is_left_alone() {
    let t = tmp("sf-twin");
    let dirs = DataDirs::at(t.0.join("data"));
    let a = t.0.join("src/HoardersHelper");
    sf_package(&a, "HoardersHelper");
    let r = library::import(&dirs, &[a.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "hoardershelper");
    let game = t.0.join("game");
    // the player extracted the same package themselves, under another folder name
    let own = game.join("mods/Hoarders Helper");
    kiln_manage::util::copy_tree(&a, &own).unwrap();
    let before = hash::dirhash(&own).unwrap();
    let mut p = profile::create(&dirs, "twin", None).unwrap();
    p.mods.shroudforge = vec![pm(&h, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.own, vec!["shroudforge:hoardershelper".to_string()], "{rep:?}");
    assert!(!game.join("mods/hoardershelper").exists(), "a second package of the same id would make ShroudForge drop both");
    assert_eq!(hash::dirhash(&own).unwrap(), before);
}

#[test]
fn a_servers_hand_placed_engine_mod_with_a_wide_id_is_listed_switched_and_advertised() {
    use kiln_manage::server::{control, instance::ServerInstance};
    let t = tmp("server-wide-id");
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    let dir = server.join("kiln/eml/mods/Hoarders Helper");
    put(&dir, "mod.json", br#"{"id":"HoardersHelper","name":"Hoarder's Helper","version":"1.0.0","capabilities":["patch"]}"#);
    put(&dir, "src/mod.lua", b"return {}");
    let inst: ServerInstance = serde_json::from_value(serde_json::json!({
        "id": "t", "name": "t", "kind": "local", "dir": server, "port": 27140, "queryPort": 27140, "wine": null,
        "policy": "any", "source": "launcher", "remote": null })).unwrap();
    // the id a player's Library imports it under
    assert_eq!(engine_mods::identity(&dir).0, "hoardershelper");
    let listed = control::mods(&inst).unwrap();
    assert!(listed.iter().any(|m| m.kind == Some(ModKind::Eml) && m.id == "hoardershelper" && m.folder == "Hoarders Helper" && m.enabled), "{listed:?}");
    assert!(modpack::required(&server).unwrap().iter().any(|m| m.id == "hoardershelper"), "joining players are told about it");
    control::set_enabled(&inst, "Hoarders Helper", false).unwrap();
    assert!(server.join("kiln/disabled/eml/Hoarders Helper/mod.json").is_file());
    control::set_enabled(&inst, "Hoarders Helper", true).unwrap();
    assert!(dir.join("mod.json").is_file());
    assert!(control::set_enabled(&inst, "../x", true).is_err());
}

#[test]
fn a_data_only_kiln_mod_imports_and_joins_a_profile() {
    let t = tmp("data-only");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/isle-colours");
    put(&src, "kiln.mod.json", br#"{"id":"isle-colours","name":"Isle Colours","version":"1.0.0","apiVersion":"0.2","side":"client"}"#);
    put(&src, "content/colours.json", br#"{"format":1}"#);
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    assert!(r.skipped.is_empty(), "{:?}", r.skipped);
    let k = refs(&r, ModKind::Kiln, "isle-colours");
    let mut p = profile::create(&dirs, "data", None).unwrap();
    p.mods.kiln = vec![pm(&k, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &game).unwrap();
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    assert_eq!(file.mods.kiln.len(), 1);
    assert_eq!(file.mods.kiln[0].id, "isle-colours");
}

/// A SET 0.2.0 mod saves `Settings.define` values to `settings.json` in its placed folder (kiln-set's modsettings), and
/// may write other files there. They are the player's: activation keeps working, keeps them across updates, and does
/// not lose them when the mod leaves the profile (docs/kiln/set.md "Settings and the launcher").
#[test]
fn set_settings_written_by_the_mod_survive_activation_updates_and_removal() {
    let t = tmp("set-settings");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/flymode");
    put(&src, "init.lua", b"Settings.define{speed={type='number',default=1}}");
    let r1 = library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap();
    let v1 = refs(&r1, ModKind::Set, "flymode");
    let mut p = profile::create(&dirs, "set-settings", None).unwrap();
    p.mods.set = vec![pm(&v1, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    let dst = game.path.join("shrouded_engine_tweaks/mods/flymode");

    // 1. first placement
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(dst.join("init.lua").is_file() && engine_mods::marker(&dst).is_some());
    assert!(a.kept_files.is_empty(), "{a:?}");

    // 2. the mod saved its settings (and a log of its own): the next activation keeps the copy and the files
    put(&dst, "settings.json", br#"{"speed":4}"#);
    put(&dst, "settings.json.bad", b"{broken");
    put(&dst, "data/visits.txt", b"3");
    let rep = engine_mods::place(&dirs, &p, &game.path).unwrap();
    assert_eq!(rep.kept, ["set:flymode"], "{rep:?}");
    let edit = |f: &dyn Fn(&mut profile::Profile)| {
        let mut x = profile::get(&dirs, &p.id).unwrap();
        f(&mut x);
        profile::save(&dirs, &x).unwrap()
    };
    let p2 = edit(&|x| x.name = "touched".into());
    profile::activate(&dirs, &p2.id, &game).unwrap();
    assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), br#"{"speed":4}"#);

    // 3. an update of the mod: the new version is placed, the player's files come along
    put(&src, "init.lua", b"Settings.define{speed={type='number',default=2}} -- 2.0");
    let v2 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::Set, "flymode");
    assert_ne!(v1.hash, v2.hash);
    let p3 = edit(&|x| x.mods.set = vec![pm(&v2, true)]);
    let a = profile::activate(&dirs, &p3.id, &game).unwrap();
    assert!(std::fs::read_to_string(dst.join("init.lua")).unwrap().ends_with("-- 2.0"));
    assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), br#"{"speed":4}"#);
    assert_eq!(std::fs::read(dst.join("data/visits.txt")).unwrap(), b"3");
    assert_eq!(engine_mods::marker(&dst).unwrap().hash, v2.hash);
    assert!(a.kept_files.is_empty(), "carried files need no sentence: {a:?}");
    // the carried files are still the player's for the receipt: the next pass keeps the copy as it is
    put(&dst, "settings.json", br#"{"speed":5}"#);
    assert_eq!(engine_mods::place(&dirs, &p3, &game.path).unwrap().kept, ["set:flymode"]);

    // 4. switched off: the copy goes, the files the mod wrote are kept under kiln/ and the result says where
    let p4 = edit(&|x| x.mods.set[0].enabled = false);
    let a = profile::activate(&dirs, &p4.id, &game).unwrap();
    assert!(!dst.exists());
    let kept = game.path.join("kiln/kept-mod-files/set/flymode");
    assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), br#"{"speed":5}"#);
    assert_eq!(std::fs::read(kept.join("data/visits.txt")).unwrap(), b"3");
    assert_eq!(a.kept_files.len(), 1, "{a:?}");
    assert!(a.kept_files[0].starts_with("set:flymode: kept the files the mod wrote (data/visits.txt, settings.json, settings.json.bad)")
        && a.kept_files[0].contains("kiln/kept-mod-files/set/flymode"), "{a:?}");

    // 5. on again: they go back into the new copy and the kept folder is gone
    let p5 = edit(&|x| x.mods.set[0].enabled = true);
    let a = profile::activate(&dirs, &p5.id, &game).unwrap();
    assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), br#"{"speed":5}"#);
    assert_eq!(std::fs::read(dst.join("data/visits.txt")).unwrap(), b"3");
    assert!(!game.path.join("kiln/kept-mod-files").exists());
    assert!(a.kept_files[0].starts_with("set:flymode: put back the files the mod wrote earlier"), "{a:?}");
    assert_eq!(engine_mods::place(&dirs, &p5, &game.path).unwrap().kept, ["set:flymode"]);

    // 6. removed from the profile (not only switched off): the same
    let p6 = edit(&|x| x.mods.set.clear());
    let a = profile::activate(&dirs, &p6.id, &game).unwrap();
    assert!(!dst.exists());
    assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), br#"{"speed":5}"#);
    assert_eq!(a.kept_files.len(), 1);
}

/// A settings file the library copy ships itself (a mod with default settings) is the player's once the mod changed
/// it; any other library file that changed still makes the copy the player's (refused, as before).
#[test]
fn shipped_settings_file_may_change_other_library_files_may_not() {
    let t = tmp("set-shipped-settings");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/glider");
    put(&src, "init.lua", b"print(1)");
    put(&src, "settings.json", br#"{"height":1}"#);
    put(&src, "readme.txt", b"v1");
    let v1 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::Set, "glider");
    let mut p = profile::create(&dirs, "shipped", None).unwrap();
    p.mods.set = vec![pm(&v1, true)];
    let game = t.0.join("game");
    engine_mods::place(&dirs, &p, &game).unwrap();
    let dst = game.join("shrouded_engine_tweaks/mods/glider");
    put(&dst, "settings.json", br#"{"height":9}"#);
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().kept, ["set:glider"]);
    // an update that ships settings.json and a file the mod also wrote (notes.txt)
    put(&dst, "notes.txt", b"mine");
    put(&src, "notes.txt", b"theirs");
    put(&src, "readme.txt", b"v2");
    let v2 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::Set, "glider");
    p.mods.set = vec![pm(&v2, true)];
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(rep.placed, ["set:glider"]);
    assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), br#"{"height":9}"#, "the player's settings win");
    assert_eq!(std::fs::read(dst.join("notes.txt")).unwrap(), b"theirs", "the new version's own file wins");
    assert_eq!(std::fs::read(game.join("kiln/kept-mod-files/set/glider/notes.txt")).unwrap(), b"mine");
    assert!(rep.kept_files[0].contains("notes.txt") && rep.kept_files[0].contains("ships its own file"), "{rep:?}");
    assert_eq!(std::fs::read(dst.join("readme.txt")).unwrap(), b"v2");
    // a changed library file that is no settings file: retained and reported, nothing removed
    put(&dst, "readme.txt", b"edited");
    p.mods.set.clear();
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert!(rep.retained.iter().any(|r| r.path == dst && r.may_run && r.reason.contains("managed copy changed")), "{rep:?}");
    assert!(rep.removed.is_empty(), "{rep:?}");
    assert_eq!(std::fs::read(dst.join("readme.txt")).unwrap(), b"edited");
}

/// Earlier values parked as `.kept-N` in kiln/kept-mod-files are recovery copies, not files the mod wrote: when the mod
/// is placed again they stay in the kept folder and do not land in the live copy.
#[test]
fn kept_n_recovery_files_stay_in_the_kept_folder_when_a_mod_comes_back() {
    let t = tmp("kept-n");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/flymode");
    put(&src, "init.lua", b"print('fly')");
    let v1 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::Set, "flymode");
    let mut p = profile::create(&dirs, "kept-n", None).unwrap();
    p.mods.set = vec![pm(&v1, true)];
    let game = t.0.join("game");
    let dst = game.join("shrouded_engine_tweaks/mods/flymode");
    let kept = game.join("kiln/kept-mod-files/set/flymode");
    // two earlier removals left the latest settings and, parked beside them, an older value
    put(&kept, "settings.json", br#"{"speed":5}"#);
    put(&kept, "settings.json.kept-1", br#"{"speed":4}"#);
    let rep = engine_mods::place(&dirs, &p, &game).unwrap();
    assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), br#"{"speed":5}"#, "{rep:?}");
    assert!(!dst.join("settings.json.kept-1").exists(), "a recovery copy is not the mod's file: {rep:?}");
    assert_eq!(std::fs::read(kept.join("settings.json.kept-1")).unwrap(), br#"{"speed":4}"#);
    assert!(!kept.join("settings.json").exists(), "the value that went back leaves the kept folder");
}

/// The `retained` list of an activation as the launcher reads it: the paths of the copies that may still run.
fn retained_paths(a: &profile::Activation) -> Vec<PathBuf> {
    let v = serde_json::to_value(a).unwrap();
    v["retained"].as_array().into_iter().flatten().filter(|r| r["mayRun"] == true && r["reason"].as_str().is_some_and(|s| !s.is_empty()))
        .map(|r| PathBuf::from(r["path"].as_str().unwrap())).collect()
}

/// A retained SF package can be disabled in its own manifest even while the profile requests it on.
#[test]
fn retained_disabled_sf_copy_is_not_reported_as_definitely_running() {
    let t = tmp("retained-disabled-sf");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let sf = refs(&r, ModKind::ShroudForge, "sfpack");
    let game = game_with_z(&t.0);
    let mut p = profile::create(&dirs, "SF", None).unwrap();
    p.mods.shroudforge.push(pm(&sf, true));
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let dst = game.path.join("mods/sfpack");
    put(&dst, "src/mod.lua", b"player code");
    kiln_manage::shroudforge::set_enabled(&dst, false).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(!kiln_manage::shroudforge::read_mod(&dst).unwrap().enabled);
    let kept = a.retained.iter().find(|r| r.path == dst).unwrap();
    assert!(kept.may_run);
    assert!(!kept.reason.contains("is what runs") && !kept.reason.contains("keeps running"),
        "a disabled retained package is not definitely running: {}", kept.reason);
    assert!(kept.reason.contains("may"), "uncertain loading must be explicit: {}", kept.reason);
    assert_eq!(std::fs::read(dst.join("src/mod.lua")).unwrap(), b"player code");
}

/// A SET or ShroudForge copy Kiln may not touch (the player edited its code, or its settings are ones ShroudForge's rules
/// refuse) is left as it is and reported; it never blocks switching profiles, removing that mod or Play.
#[test]
fn one_sf_or_set_copy_kiln_cannot_touch_never_blocks_activation_removal_or_play() {
    use kiln_manage::launch::{LaunchMode, LaunchRequest};
    let t = tmp("untouchable-copy");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let (sf, set) = (refs(&r, ModKind::ShroudForge, "sfpack"), refs(&r, ModKind::Set, "flymode"));
    let mut p = profile::create(&dirs, "both", None).unwrap();
    p.mods.shroudforge = vec![pm(&sf, true)];
    p.mods.set = vec![pm(&set, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let other = profile::create(&dirs, "other", None).unwrap();
    let game = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &game).unwrap();
    let set_copy = game.path.join("shrouded_engine_tweaks/mods/flymode");
    let sf_copy = game.path.join("mods/sfpack");
    // the player tuned SET's code in place; ShroudForge's editor left a setting its own rules refuse
    put(&set_copy, "init.lua", b"print('tuned')");
    put(&sf_copy, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":true,"targets":["client","server"],
        "settings":{"speed":{"label":"Speed","control":"slider","min":1,"max":10,"value":null}}}"#);
    // the same profile again, Play, another profile, the mods removed: all go on and report both copies
    let a = profile::activate(&dirs, &p.id, &game);
    assert!(a.is_ok(), "the same profile: {:?}", a.err());
    let a = a.unwrap();
    assert!(retained_paths(&a).contains(&set_copy) && retained_paths(&a).contains(&sf_copy), "{a:?}");
    let req = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
    let mut edited = profile::get(&dirs, &p.id).unwrap();
    edited.name = "edited".into();
    profile::save(&dirs, &edited).unwrap();
    let r = kiln_manage::launch::play_with(&dirs, &game, &req, Some(&[]), &|_| Ok(()));
    assert!(r.is_ok(), "Play: {:?}", r.err());
    let a = profile::activate(&dirs, &other.id, &game);
    assert!(a.is_ok(), "another profile: {:?}", a.err());
    assert!(retained_paths(&a.unwrap()).contains(&set_copy));
    let mut gone = profile::get(&dirs, &p.id).unwrap();
    gone.mods.set.clear();
    gone.mods.shroudforge.clear();
    let gone = profile::save(&dirs, &gone).unwrap();
    let a = profile::activate(&dirs, &gone.id, &game);
    assert!(a.is_ok(), "the mods removed: {:?}", a.err());
    // what still runs: Kiln's SET plugin runs every mod in its folder; ShroudForge may still load its package
    let a = serde_json::to_value(a.unwrap()).unwrap();
    let reason = |p: &Path| a["retained"].as_array().unwrap().iter().find(|r| r["path"] == serde_json::json!(p))
        .and_then(|r| r["reason"].as_str()).unwrap_or_default().to_string();
    assert!(reason(&set_copy).contains("It is off in the profile, but Kiln's SET plugin runs every mod in that folder"), "{a}");
    assert!(reason(&sf_copy).contains("It is off in the profile, but ShroudForge may still load it"), "{a}");
    assert_eq!(std::fs::read(set_copy.join("init.lua")).unwrap(), b"print('tuned')");
    assert!(std::fs::read_to_string(sf_copy.join("extended.mod.json")).unwrap().contains("\"value\":null"));
}

/// A drop that turns a remembered profile back on activates twice (the resume, then the profile with the new mod):
/// what the first activation put back is still in the result.
#[test]
fn a_resumed_profiles_kept_files_survive_the_add_that_resumed_it() {
    let t = tmp("resume-notes");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let set = refs(&r, ModKind::Set, "flymode");
    let mut p = profile::create(&dirs, "set", None).unwrap();
    p.mods.set = vec![pm(&set, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &game).unwrap();
    put(&game.path.join("shrouded_engine_tweaks/mods/flymode"), "settings.json", br#"{"speed":4}"#);
    let edit = |on: bool| {
        let mut x = profile::get(&dirs, &p.id).unwrap();
        x.mods.set[0].enabled = on;
        profile::save(&dirs, &x).unwrap()
    };
    // switched off: the settings wait in kiln/kept-mod-files; on again in the launcher, then Kiln is uninstalled
    profile::activate(&dirs, &edit(false).id, &game).unwrap();
    edit(true);
    std::fs::remove_file(game.path.join("kiln/profile.json")).unwrap();
    let k = t.0.join("src2/isle-colours");
    put(&k, "kiln.mod.json", br#"{"id":"isle-colours","name":"Isle Colours","version":"1.0.0","apiVersion":"0.2","side":"client"}"#);
    put(&k, "content/colours.json", br#"{"format":1}"#);
    let r = kiln_manage::quick::add_with(&dirs, &game, &[k], false, &mut NoProgress).unwrap();
    assert!(r.resumed, "{r:?}");
    let a = r.activation.expect("activated");
    assert!(a.kept_files.iter().any(|s| s.starts_with("set:flymode: put back")), "{a:?}");
}

/// A ShroudForge package in the Library that the reader refuses (imported by dev.23 with the laxer reader; the import
/// gate refuses new ones): activation does not place it, names it, and places the profile's other mods.
#[test]
fn a_library_package_shroudforge_would_refuse_is_not_placed_and_stops_nothing() {
    let t = tmp("sf-refused-entry");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let (sf, set) = (refs(&r, ModKind::ShroudForge, "sfpack"), refs(&r, ModKind::Set, "flymode"));
    // the entry as it was imported then: a setting value that is an object
    let old = library::path_of(&dirs, &sf).unwrap();
    std::fs::write(old.join("extended.mod.json"), br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"targets":["client","server"],
        "settings":{"speed":{"label":"Speed","control":"slider","min":1,"max":10,"value":{"x":3}}}}"#).unwrap();
    let hash = hash::dirhash(&old).unwrap();
    let new = dirs.library_entry(ModKind::ShroudForge, &sf.id, &sf.version, &hash).unwrap();
    std::fs::rename(&old, &new).unwrap();
    let record = |p: &Path| PathBuf::from(format!("{}.json", p.display()));
    let mut rec: serde_json::Value = serde_json::from_slice(&std::fs::read(record(&old)).unwrap()).unwrap();
    rec["hash"] = hash.clone().into();
    std::fs::write(record(&new), serde_json::to_vec(&rec).unwrap()).unwrap();
    std::fs::remove_file(record(&old)).unwrap();
    let sf = LibraryRef { hash, ..sf };
    assert!(kiln_manage::shroudforge::read_mod(&library::path_of(&dirs, &sf).unwrap()).is_err(), "the reader refuses it");
    let mut p = profile::create(&dirs, "refused", None).unwrap();
    p.mods.shroudforge = vec![pm(&sf, true)];
    p.mods.set = vec![pm(&set, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    let a = profile::activate(&dirs, &p.id, &game);
    assert!(a.is_ok(), "{:?}", a.err());
    let a = a.unwrap();
    assert!(game.path.join("shrouded_engine_tweaks/mods/flymode/init.lua").is_file(), "the other mods are placed");
    assert!(!game.path.join("mods/sfpack").exists());
    assert!(a.warnings.iter().any(|w| w.contains("shroudforge:sfpack") && w.contains("cannot be read")), "{a:?}");
}

/// Server sync carries a ShroudForge mod's settings into its new version like activation does: a value the new version
/// refuses keeps the new default and is reported, and the mods after it are still deployed.
#[test]
fn server_sync_keeps_the_new_default_for_a_refused_shroudforge_setting() {
    use kiln_manage::server::{control, instance::ServerInstance};
    struct Said(Vec<String>);
    impl kiln_manage::task::Progress for Said {
        fn report(&mut self, _: Option<f32>, message: &str) { self.0.push(message.to_string()); }
    }
    let t = tmp("server-sf-refused");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = sources(&t.0);
    let r = library::import(&dirs, &src, &mut NoProgress).unwrap();
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    let inst: ServerInstance = serde_json::from_value(serde_json::json!({
        "id": "t", "name": "t", "kind": "local", "dir": server, "port": 27140, "queryPort": 27140, "wine": null,
        "policy": "any", "source": "launcher", "remote": null })).unwrap();
    control::sync(&dirs, &inst, &[refs(&r, ModKind::ShroudForge, "sfpack")], &mut NoProgress).unwrap();
    let copy = server.join("mods/sfpack");
    kiln_manage::shroudforge::set_setting(&copy, "speed", &serde_json::json!(7)).unwrap();
    put(&src[1], "mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"2.0.0"}"#);
    put(&src[1], "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"settings":{"speed":{"value":1,"min":0,"max":2}}}"#);
    let v2 = refs(&library::import(&dirs, std::slice::from_ref(&src[1]), &mut NoProgress).unwrap(), ModKind::ShroudForge, "sfpack");
    let mut said = Said(Vec::new());
    let r2 = control::sync(&dirs, &inst, &[v2, refs(&r, ModKind::Set, "flymode")], &mut said);
    assert!(r2.is_ok(), "{:?}", r2.err());
    assert_eq!(kiln_manage::shroudforge::read_mod(&copy).unwrap().version, "2.0.0");
    assert_eq!(kiln_manage::shroudforge::read_mod(&copy).unwrap().settings[0].value, 1);
    assert!(server.join("shrouded_engine_tweaks/mods/flymode/init.lua").is_file(), "the next mod is deployed");
    assert!(said.0.iter().any(|m| m.contains("speed") && m.contains('7')), "{:?}", said.0);
}

/// ShroudForge packages placed through the same code: a file the mod writes next to its package is the player's too.
#[test]
fn shroudforge_files_written_by_the_mod_are_kept_like_sets() {
    let t = tmp("sf-written");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = sources(&t.0)[1].clone();
    let v1 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::ShroudForge, "sfpack");
    let mut p = profile::create(&dirs, "sf-written", None).unwrap();
    p.mods.shroudforge = vec![pm(&v1, true)];
    let game = t.0.join("game");
    engine_mods::place(&dirs, &p, &game).unwrap();
    let dst = game.join("mods/sfpack");
    put(&dst, "cache/state.json", b"{}");
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().kept, ["shroudforge:sfpack"]);
    put(&src, "mod.json", br#"{"id":"sfpack","name":"SF Pack","version":"2.0.0"}"#);
    let v2 = refs(&library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap(), ModKind::ShroudForge, "sfpack");
    p.mods.shroudforge = vec![pm(&v2, true)];
    assert_eq!(engine_mods::place(&dirs, &p, &game).unwrap().placed, ["shroudforge:sfpack"]);
    assert_eq!(std::fs::read(dst.join("cache/state.json")).unwrap(), b"{}");
    assert_eq!(kiln_manage::shroudforge::read_mod(&dst).unwrap().version, "2.0.0");
}

/// Player report r1110: a native mod in the Library that is SET's own loader (imported before the Library recognised
/// loaders). The launcher's plan for the profile says so; the import of SET's release zip leaves the loader out
/// (import_shapes.rs set_release_zip_leaves_its_loader_out_and_imports_its_mods).
#[test]
fn a_native_mod_that_is_sets_loader_is_named_in_the_profile_plan() {
    let t = tmp("native-set-loader");
    let dirs = DataDirs::at(t.0.join("data"));
    put(&t.0, "src/powrprof.dll", &build_test_dll(&["PowerGetActiveScheme"], b"a plain power proxy"));
    let r = library::import(&dirs, &[t.0.join("src/powrprof.dll")], &mut NoProgress).unwrap();
    let native = r.imported.iter().find(|x| x.kind == ModKind::Native).cloned().unwrap_or_else(|| panic!("{r:?}"));
    let mut p = profile::create(&dirs, "r1110", None).unwrap();
    p.mods.native = vec![pm(&native, true)];
    let pre = profile::resolve_preview(&dirs, &p, kiln_manage::manifest::RunSide::Client).unwrap();
    assert!(!pre.warnings.iter().any(|w| w.contains("own loader")), "{:?}", pre.warnings);
    // the entry as an old launcher imported it: SET's loader
    let folder = library::path_of(&dirs, &native).unwrap();
    std::fs::write(folder.join("powrprof.dll"), build_test_dll(&["PowerGetActiveScheme"], b"VK_LAYER_SHROUDED_ENGINE_TWEAKS")).unwrap();
    let pre = profile::resolve_preview(&dirs, &p, kiln_manage::manifest::RunSide::Client).unwrap();
    assert!(pre.warnings.iter().any(|w| w.starts_with(&format!("Skipped native mod {}: powrprof.dll is Shrouded Engine Tweaks's own loader", native.id))),
        "{:?}", pre.warnings);
    assert!(!pre.native[0].enabled && pre.native[0].reason.as_ref().unwrap().contains("Library source"));
    let game = game_with_z(&t.0);
    let p = profile::save(&dirs, &p).unwrap();
    let source_hash = hash::dirhash(&folder).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(a.warnings, pre.warnings);
    assert!(!profile::read_game_profile(&game.path).unwrap().unwrap().mods.native[0].enabled);
    assert!(profile::get(&dirs, &p.id).unwrap().mods.native[0].enabled, "the stored selection is unchanged");
    assert_eq!(hash::dirhash(&folder).unwrap(), source_hash, "no source bytes removed or rewritten");
    assert!(!std::fs::read_to_string(game.path.join("kiln/chain.ini")).unwrap().contains("powrprof.dll"));
    // The same render rule covers play-once and copy-mode profiles, not just persistent activation.
    assert!(!profile::render(&dirs, &p, &game, PathMode::Copy).unwrap().mods.native[0].enabled);
    let mut p = p;
    p.mods.native[0].enabled = false;
    assert!(profile::resolve_preview(&dirs, &p, kiln_manage::manifest::RunSide::Client).unwrap().warnings.is_empty());
}

/// A placed ShroudForge copy whose fractional setting an earlier Kiln wrote without its fraction: ShroudForge refuses the
/// whole mod until the file is written again. The copy already has the switch the profile wants, so no switch is due;
/// the next activation repairs it all the same, and says so.
#[test]
fn a_placed_shroudforge_copy_that_needs_repair_is_repaired_when_its_switch_is_already_right() {
    let t = tmp("sf-repair-same-switch");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/ratio");
    put(&src, "mod.json", br#"{"id":"ratio","name":"Ratio","version":"1.0.0"}"#);
    put(&src, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":true,"targets":["client","server"],
        "settings":{"speed":{"label":"Speed","value":1.5,"options":{"1.5":"Slow","2.0":"Fast"}}}}"#);
    put(&src, "src/mod.lua", b"return {}");
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    let sf = refs(&r, ModKind::ShroudForge, "ratio");
    let mut p = profile::create(&dirs, "repair", None).unwrap();
    p.mods.shroudforge = vec![pm(&sf, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let game = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &game).unwrap();
    let copy = kiln_manage::shroudforge::mods_dir(&game.path).join("ratio");
    assert!(kiln_manage::shroudforge::read_mod(&copy).unwrap().enabled, "placed switched on");
    // what an earlier Kiln left after the player chose "Fast": the whole number without its fraction
    let damaged = std::fs::read_to_string(copy.join("extended.mod.json")).unwrap().replace("\"value\":1.5", "\"value\":2");
    assert!(damaged.contains("\"value\":2,"), "{damaged}");
    std::fs::write(copy.join("extended.mod.json"), &damaged).unwrap();
    assert_eq!(kiln_manage::shroudforge::read_mod(&copy).unwrap().repair, ["speed"], "recognised as waiting for repair");
    // the same profile again (what Play does): nothing to switch, but the copy is repaired
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    let after = kiln_manage::shroudforge::read_mod(&copy).unwrap();
    assert!(after.repair.is_empty(), "still waiting for repair: {}", std::fs::read_to_string(copy.join("extended.mod.json")).unwrap());
    assert!(after.enabled, "the switch is unchanged");
    assert!(std::fs::read_to_string(copy.join("extended.mod.json")).unwrap().contains("\"value\":2.0"), "the player's choice is kept, with its fraction");
    assert!(a.warnings.iter().any(|w| w.contains("shroudforge:ratio") && w.contains("repaired")), "{a:?}");
    // and it is said once: the next activation has nothing to repair
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(!a.warnings.iter().any(|w| w.contains("repaired")), "{a:?}");
}

/// What ShroudForge's own Modloader writes when the player saves a setting (or flips the switch) in its in-game menu
/// (kiln-shroudforge modules/modloader update_mod_configuration.rs): the new value, and `targets` with ShroudForge's
/// default right after `enabled` when the file has none (["client"], or ["client", "server"] for launcher EML), as
/// pretty JSON.
fn sf_modloader_save(dir: &Path, key: &str, value: serde_json::Value) {
    let file = dir.join("extended.mod.json");
    let doc: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&file).unwrap()).unwrap();
    let obj = doc.as_object().unwrap();
    let eml = obj.get("launcher").and_then(|l| l.as_str()) == Some("EML");
    let mut out = serde_json::Map::new();
    for (k, v) in obj {
        out.insert(k.clone(), v.clone());
        if k == "enabled" && !obj.contains_key("targets") {
            out.insert("targets".into(), if eml { serde_json::json!(["client", "server"]) } else { serde_json::json!(["client"]) });
        }
    }
    let s = out.get_mut("settings").unwrap().get_mut(key).unwrap();
    if s.is_object() { s["value"] = value; } else { *s = value; }
    std::fs::write(&file, serde_json::to_vec_pretty(&serde_json::Value::Object(out)).unwrap()).unwrap();
}

/// A player with only Kiln's own copies changes a setting in ShroudForge's in-game menu: ShroudForge writes the file
/// with its default `targets` added. The copy is still the one Kiln placed: the next activation keeps it as Kiln's
/// (no "Some mod folders were kept" notice), and the launcher can still edit its settings and switch it.
#[test]
fn shroudforges_own_menu_writing_a_kiln_copy_leaves_it_kilns() {
    let t = tmp("sf-own-menu");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/plainpack");
    sf_package(&src, "plainpack"); // as most packages ship: no `targets`
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "plainpack");
    let game = game_with_z(&t.0);
    let mut p = profile::create(&dirs, "menu", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let mut p = profile::save(&dirs, &p).unwrap();
    let first = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(first.retained.is_empty(), "{first:?}");
    let dst = game.path.join("mods/plainpack");
    sf_modloader_save(&dst, "speed", serde_json::json!(5));
    assert!(std::fs::read_to_string(dst.join("extended.mod.json")).unwrap().contains("\"targets\""));
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(a.retained.is_empty(), "ShroudForge's own write made Kiln's copy look like the player's: {:?}", a.retained);
    assert_eq!(a.warnings, first.warnings);
    let cfg = t.0.join("config");
    let s = engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(7), Some(&game.path), &cfg, None).unwrap();
    assert_eq!(s.values["speed"], 7);
    assert!(!s.own_folder);
    p.mods.shroudforge[0].enabled = false;
    let p = profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(a.retained.is_empty(), "{:?}", a.retained);
    let m = kiln_manage::shroudforge::read_mod(&dst).unwrap();
    assert!(!m.enabled, "switched off in the profile, so off in ShroudForge's file");
    assert_eq!(m.settings[0].value, 7, "the value stays");
}

/// The player's own ShroudForge folder (extracted before Kiln, then "Add it" in the launcher): Kiln never switches,
/// replaces or removes it, but a setting the player changes in the launcher is written there, that value only.
#[test]
fn a_players_own_shroudforge_folder_takes_a_launcher_setting_and_nothing_else() {
    let t = tmp("sf-own-setting");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/HoardersHelper");
    sf_package(&src, "HoardersHelper");
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "hoardershelper");
    let lib = library::path_of(&dirs, &h).unwrap();
    let game = game_with_z(&t.0);
    let own = game.path.join("mods/hoardershelper");
    kiln_manage::util::copy_tree(&src, &own).unwrap();
    let mut p = profile::create(&dirs, "own", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let mut p = profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(a.retained.is_empty(), "{a:?}");
    assert!(a.warnings.iter().any(|w| w.contains("your own folder")), "{a:?}");
    let ext = own.join("extended.mod.json");
    let before = std::fs::read_to_string(&ext).unwrap();
    let cfg = t.0.join("config");
    let s = engine_mods::settings(ModKind::ShroudForge, &h.id, Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert!(s.own_folder, "{s:?}");
    assert_eq!(s.values["speed"], 3);
    let s = engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(9), Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(s.values["speed"], 9);
    assert!(s.own_folder);
    let after = std::fs::read_to_string(&ext).unwrap();
    assert_eq!(after, before.replace("\"value\":3", "\"value\":9"), "only that value changed");
    assert!(!own.join(engine_mods::MARKER).exists(), "Kiln did not take the folder over");
    // what ShroudForge refuses is refused here too, and the file stays
    assert!(engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(99), Some(&game.path), &cfg, Some(&lib)).is_err());
    assert!(engine_mods::set_setting(ModKind::ShroudForge, &h.id, "nope", &serde_json::json!(1), Some(&game.path), &cfg, Some(&lib)).is_err());
    assert_eq!(std::fs::read_to_string(&ext).unwrap(), after);
    // switching it off in the profile still leaves the folder as it is
    p.mods.shroudforge[0].enabled = false;
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(std::fs::read_to_string(&ext).unwrap(), after);
    assert!(!kiln_manage::shroudforge::read_mod(&own).unwrap().enabled, "never switched");
}

/// The player's own copy under another folder name (ShroudForge finds packages by id): the launcher reads and writes
/// the settings of that folder, not the Library's defaults.
#[test]
fn a_players_own_shroudforge_folder_under_another_name_shows_its_own_settings() {
    let t = tmp("sf-own-elsewhere");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/HoardersHelper");
    sf_package(&src, "HoardersHelper");
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "hoardershelper");
    let lib = library::path_of(&dirs, &h).unwrap();
    let game = game_with_z(&t.0);
    let own = game.path.join("mods/Hoarders Helper");
    kiln_manage::util::copy_tree(&src, &own).unwrap();
    sf_modloader_save(&own, "speed", serde_json::json!(4));
    let mut p = profile::create(&dirs, "own", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let cfg = t.0.join("config");
    let s = engine_mods::settings(ModKind::ShroudForge, &h.id, Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(s.values["speed"], 4, "the player's folder's value, not the Library's: {s:?}");
    assert!(s.own_folder);
    assert_eq!(s.file.as_deref(), Some(own.join("extended.mod.json").to_str().unwrap()));
    engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(6), Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(kiln_manage::shroudforge::read_mod(&own).unwrap().settings[0].value, 6);
    assert!(!game.path.join("mods/hoardershelper").exists());
}

/// A kept copy says whether the profile has its mod off (`off`): the launcher's one notice that shows even with the
/// notes about mod folders switched off is a folder that may still run although the mod is off.
#[test]
fn a_kept_copy_says_whether_its_mod_is_off_in_the_profile() {
    let t = tmp("retained-off");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let sf = refs(&r, ModKind::ShroudForge, "sfpack");
    let game = game_with_z(&t.0);
    let mut p = profile::create(&dirs, "SF", None).unwrap();
    p.mods.shroudforge.push(pm(&sf, true));
    let mut p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let dst = game.path.join("mods/sfpack");
    put(&dst, "src/mod.lua", b"player code");
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    let kept = a.retained.iter().find(|r| r.path == dst).unwrap();
    assert!(kept.may_run && !kept.off, "{kept:?}");
    p.mods.shroudforge[0].enabled = false;
    let p = profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    let kept = a.retained.iter().find(|r| r.path == dst).unwrap();
    assert!(kept.may_run && kept.off, "{kept:?}");
    let json = serde_json::to_value(kept).unwrap();
    assert_eq!(json["off"], true);
}

/// A setting write never destroys a file of the mod folder: a file that happens to be called
/// `extended.mod.json.tmp` (the player's, or a hard link to one) stays as it is, in Kiln's copy and in the player's own.
#[test]
fn a_setting_write_leaves_a_players_tmp_file_alone() {
    let t = tmp("sf-tmp-name");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/plainpack");
    sf_package(&src, "plainpack");
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "plainpack");
    let game = game_with_z(&t.0);
    let mut p = profile::create(&dirs, "tmp", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let dst = game.path.join("mods/plainpack");
    let outside = t.0.join("player-notes.txt");
    std::fs::write(&outside, b"the player's notes").unwrap();
    std::fs::hard_link(&outside, dst.join("extended.mod.json.tmp")).unwrap();
    let cfg = t.0.join("config");
    engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(4), Some(&game.path), &cfg, None).unwrap();
    assert_eq!(std::fs::read(&outside).unwrap(), b"the player's notes", "the linked file was truncated");
    assert_eq!(std::fs::read(dst.join("extended.mod.json.tmp")).unwrap(), b"the player's notes");
    assert_eq!(kiln_manage::shroudforge::read_mod(&dst).unwrap().settings[0].value, 4);
    // no temp file of Kiln's is left behind
    let left: Vec<_> = std::fs::read_dir(&dst).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| n.contains("tmp") && n != "extended.mod.json.tmp").collect();
    assert!(left.is_empty(), "{left:?}");
}

/// A profile entry's settings are read from and written to the folder that holds THAT package: a player's folder named
/// like the entry's Library id that holds another package is not it.
#[test]
fn settings_go_to_the_folder_holding_that_package_only() {
    let t = tmp("sf-right-folder");
    let dirs = DataDirs::at(t.0.join("data"));
    let a = t.0.join("src/PackA");
    sf_package(&a, "PackA");
    let r = library::import(&dirs, &[a.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "packa");
    let lib = library::path_of(&dirs, &h).unwrap();
    let game = game_with_z(&t.0);
    // the player's own copy of A under another name, and the player's package B in a folder called "packa"
    let own_a = game.path.join("mods/Pack A");
    kiln_manage::util::copy_tree(&a, &own_a).unwrap();
    sf_modloader_save(&own_a, "speed", serde_json::json!(4));
    let b = game.path.join("mods/packa");
    sf_package(&b, "PackB");
    let b_before = std::fs::read(b.join("extended.mod.json")).unwrap();
    let mut p = profile::create(&dirs, "two", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &game).unwrap();
    let cfg = t.0.join("config");
    let s = engine_mods::settings(ModKind::ShroudForge, &h.id, Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(s.file.as_deref(), Some(own_a.join("extended.mod.json").to_str().unwrap()), "{s:?}");
    assert_eq!(s.values["speed"], 4);
    engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(6), Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(kiln_manage::shroudforge::read_mod(&own_a).unwrap().settings[0].value, 6);
    assert_eq!(std::fs::read(b.join("extended.mod.json")).unwrap(), b_before, "package B's file was written");
    // two folders of the player's hold package A: nothing is written, and both are named
    let twin = game.path.join("mods/Pack A copy");
    kiln_manage::util::copy_tree(&a, &twin).unwrap();
    let e = engine_mods::set_setting(ModKind::ShroudForge, &h.id, "speed", &serde_json::json!(7), Some(&game.path), &cfg, Some(&lib)).unwrap_err();
    assert!(e.message.contains("Pack A copy") && e.message.contains("mods/Pack A"), "{}", e.message);
    assert_eq!(kiln_manage::shroudforge::read_mod(&own_a).unwrap().settings[0].value, 6);
    assert_eq!(kiln_manage::shroudforge::read_mod(&twin).unwrap().settings[0].value, 3);
    let s = engine_mods::settings(ModKind::ShroudForge, &h.id, Some(&game.path), &cfg, Some(&lib)).unwrap();
    assert_eq!(s.format, "none", "{s:?}");
    assert!(s.note.as_deref().is_some_and(|n| n.contains("Pack A copy")), "{s:?}");
}

/// The player's own folder of a mod the profile has OFF may still run (Kiln never switches it): its note is a safety
/// note, said even with the notes about mod folders switched off.
#[test]
fn a_players_own_folder_of_a_mod_that_is_off_is_a_safety_note() {
    let t = tmp("sf-own-off");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/HoardersHelper");
    sf_package(&src, "HoardersHelper");
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    let h = refs(&r, ModKind::ShroudForge, "hoardershelper");
    let game = game_with_z(&t.0);
    kiln_manage::util::copy_tree(&src, &game.path.join("mods/hoardershelper")).unwrap();
    let mut p = profile::create(&dirs, "own", None).unwrap();
    p.mods.shroudforge.push(pm(&h, true));
    let mut p = profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert!(a.safety_notes.is_empty(), "on: no safety note {a:?}");
    p.mods.shroudforge[0].enabled = false;
    let p = profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &p.id, &game).unwrap();
    assert_eq!(a.safety_notes.len(), 1, "{a:?}");
    assert!(a.safety_notes[0].contains("stays as it is") && a.warnings.contains(&a.safety_notes[0]), "{a:?}");
    assert_eq!(serde_json::to_value(&a).unwrap()["safetyNotes"][0], serde_json::json!(a.safety_notes[0]));
}
