//! Enshwick, shroudlink and Ember mods managed like the other engine kinds (engine_mods.rs, engine_import_security.rs,
//! engine_server_review.rs): Library import (ids, versions, kdh1, the files each plugin needs, links and bad ids
//! refused), profiles (the paths kiln-enshwick, kiln-shroudlink and kiln-ember read), server sync into each engine's
//! own folder, status files, settings notes, and modpacks (only what joining players need).
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::engine_mods;
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, DetectKind, LibraryRef};
use kiln_manage::manifest::{Clients, ModKind, Side};
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, hash, modpack};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-engine-esl-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

const ENSHWICK_EXPORTS: [&str; 4] = ["enshwick_mod_abi", "enshwick_mod_query", "enshwick_mod_init", "enshwick_mod_ready"];

/// One mod of each new kind, as their authors ship them.
fn sources(root: &Path) -> Vec<PathBuf> {
    // Enshwick data pack (pack.toml names it) and Luau mod (pack.toml + main.luau, server side)
    put(root, "src/lanterns/pack.toml", b"id = \"you.lanterns\"\nversion = \"1.0.0\"\nname = \"Lanterns\"\n\n[requires]\nx = \"you.x\"\n");
    put(root, "src/lanterns/items.toml", b"[[item]]\nid = \"lantern\"\n");
    put(root, "src/greeter/pack.toml", b"id = \"you.greeter\"\nversion = \"0.2.0\"\nside = \"server\"\n");
    put(root, "src/greeter/main.luau", b"print('hi')");
    // an Enshwick DLL mod dropped alone, and a shroudlink mod DLL dropped alone
    put(root, "src/lockers.dll", &build_test_dll(&ENSHWICK_EXPORTS, b""));
    put(root, "src/chestsort.dll", &build_test_dll(&["shroudlink_mod_v2"], b""));
    // a shroudlink release's shroudlink_mods/ folder: a mod DLL and an EML data mod (kiln-eml's)
    put(root, "src/meter/shroudlink_mods/meter.dll", &build_test_dll(&["shroudlink_mod_v1"], b""));
    put(root, "src/meter/shroudlink_mods/stacks/mod.json", br#"{"id":"stacks","name":"Stacks x10","version":"1.0.0"}"#);
    put(root, "src/meter/shroudlink_mods/stacks/src/mod.lua", b"return {}");
    // an Ember mod as Ember lays it out: ember/mods/<mod>/ with one library and its config
    put(root, "src/ember-zip/ember/mods/private-chests/private_chests.dll", &build_test_dll(&[], b""));
    put(root, "src/ember-zip/ember/mods/private-chests/config.json", b"{}");
    ["lanterns", "greeter", "lockers.dll", "chestsort.dll", "meter/shroudlink_mods", "ember-zip"].iter().map(|r| root.join("src").join(r)).collect()
}

fn refs(r: &library::ImportReport, kind: ModKind, id: &str) -> LibraryRef {
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned()
        .unwrap_or_else(|| panic!("{kind:?} {id} not imported: {r:?}"))
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
fn enshwick_shroudlink_and_ember_mods_are_imported_profiled_and_rendered() {
    let t = tmp("flow");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    assert!(r.skipped.is_empty(), "{:?}", r.skipped);
    let lanterns = refs(&r, ModKind::Enshwick, "you.lanterns");
    let greeter = refs(&r, ModKind::Enshwick, "you.greeter");
    let lockers = refs(&r, ModKind::Enshwick, "lockers");
    let chestsort = refs(&r, ModKind::Shroudlink, "chestsort");
    let meter = refs(&r, ModKind::Shroudlink, "meter");
    let stacks = refs(&r, ModKind::Eml, "stacks");
    let chests = refs(&r, ModKind::Ember, "private-chests");
    assert_eq!((lanterns.version.as_str(), greeter.version.as_str(), lockers.version.as_str()), ("1.0.0", "0.2.0", "0.0.0"));
    // content addressed like every kind: library/<kind>/<id>/<version>+<hash8>, kdh1 of the folder, side and clients
    for (x, side, clients) in [
        (&lanterns, Side::Both, Clients::Mod), (&greeter, Side::Server, Clients::None), (&lockers, Side::Both, Clients::Mod),
        (&chestsort, Side::Server, Clients::None), (&meter, Side::Server, Clients::None), (&chests, Side::Server, Clients::None),
    ] {
        let folder = library::path_of(&dirs, x).unwrap();
        assert!(folder.starts_with(dirs.library().join(x.kind.as_str())), "{}", folder.display());
        assert_eq!(hash::dirhash(&folder).unwrap(), x.hash);
        let rec = library::record(&dirs, x).unwrap();
        assert_eq!((rec.side, rec.clients), (Some(side), Some(clients)), "{x:?}");
        // re-importing the same bytes is the same entry
        assert!(library::import(&dirs, &[folder], &mut NoProgress).unwrap().imported.iter().all(|i| i.kind != x.kind || i.id != x.id));
    }
    // lone DLLs are wrapped into a folder with nothing generated: Enshwick's and shroudlink's manifests are in the DLL
    for (x, file) in [(&lockers, "lockers.dll"), (&chestsort, "chestsort.dll")] {
        let folder = library::path_of(&dirs, x).unwrap();
        let names: Vec<_> = std::fs::read_dir(&folder).unwrap().flatten().map(|e| e.file_name().into_string().unwrap()).collect();
        assert_eq!(names, [file]);
    }
    // the shroudlink release folder: the mod is named after its DLL, its EML data mod went to kiln-eml's kind
    assert!(library::path_of(&dirs, &meter).unwrap().join("meter.dll").is_file());
    assert!(!library::path_of(&dirs, &meter).unwrap().join("stacks").exists());
    assert!(library::path_of(&dirs, &stacks).unwrap().join("src/mod.lua").is_file());
    assert!(library::path_of(&dirs, &chests).unwrap().join("private_chests.dll").is_file());
    // the Library shows pack.toml's name
    let listed = library::list(&dirs, Some(ModKind::Enshwick)).unwrap();
    let lantern_entry = listed.iter().find(|e| e.id == "you.lanterns").unwrap();
    assert_eq!(lantern_entry.versions[0].manifest.as_ref().unwrap()["name"], "Lanterns");

    // a profile with all of them: each plugin's list holds the library folder (kiln-enshwick copies it into
    // <game>/mods/<id>/ itself; kiln-shroudlink and kiln-ember read it in place)
    let mut p = profile::create(&dirs, "New engines", None).unwrap();
    p.mods.enshwick = vec![pm(&lanterns, true), pm(&greeter, true), pm(&lockers, false)];
    p.mods.shroudlink = vec![pm(&chestsort, true), pm(&meter, true)];
    p.mods.ember = vec![pm(&chests, true)];
    p.mods.eml = vec![pm(&stacks, true)];
    let p = profile::save(&dirs, &p).unwrap();
    let counts = &profile::list(&dirs).unwrap()[0].counts;
    assert_eq!((counts.enshwick, counts.shroudlink, counts.ember), (3, 2, 1));
    let game = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &game).unwrap();
    let file = profile::read_game_profile(&game.path).unwrap().unwrap();
    let z = |p: &Path| format!("Z:{}", p.display()).replace('/', "\\");
    for (list, x) in [(&file.mods.enshwick, &lanterns), (&file.mods.enshwick, &lockers), (&file.mods.shroudlink, &meter), (&file.mods.ember, &chests)] {
        let entry = list.iter().find(|m| m.id == x.id).unwrap();
        assert_eq!(entry.path, z(&library::path_of(&dirs, x).unwrap()));
        assert_eq!(entry.hash.as_deref(), Some(x.hash.as_str()));
    }
    assert!(!file.mods.enshwick.iter().find(|m| m.id == "lockers").unwrap().enabled);
    let text = std::fs::read_to_string(game.path.join("kiln/profile.json")).unwrap();
    assert!(text.contains("\"enshwick\"") && text.contains("\"shroudlink\"") && text.contains("\"ember\""));
    // kiln-manage places nothing for these kinds: kiln-enshwick syncs <game>/mods itself, with its own receipts
    assert!(!game.path.join("mods/you.lanterns").exists());
    // copy mode: the relative copy path under kiln/, as the plugins resolve it
    let copied = profile::render(&dirs, &p, &game, PathMode::Copy).unwrap();
    assert_eq!(copied.mods.enshwick[0].path, format!("profiles/{}/enshwick/you.lanterns", p.id));
    assert_eq!(copied.mods.ember[0].path, format!("profiles/{}/ember/private-chests", p.id));

    // settings: nothing Kiln edits, each with its reason
    let cfg = dirs.profile(&p.id).join("config");
    let s = engine_mods::settings(ModKind::Enshwick, "you.lanterns", Some(&game.path), &cfg, None).unwrap();
    assert!(s.format == "none" && s.note.as_deref().unwrap().contains("per world"), "{s:?}");
    let s = engine_mods::settings(ModKind::Ember, "private-chests", Some(&game.path), &cfg, None).unwrap();
    assert!(s.note.as_deref().unwrap().contains("don't run yet"), "{s:?}");
    assert_eq!(engine_mods::set_setting(ModKind::Shroudlink, "meter", "x", &serde_json::json!(1), None, &cfg, None).unwrap_err().code,
               ErrorCode::Unsupported);
}

#[test]
fn new_kinds_report_their_plugins_state_files() {
    let t = tmp("status");
    put(&t.0, "kiln/state/enshwick.json", br#"{"status":"running","mods":[{"folder":"you.lanterns","action":"copy"}]}"#);
    put(&t.0, "kiln/state/shroudlink.json", br#"{"started":true}"#);
    put(&t.0, "kiln/state/ember.json", br#"{"abi":null,"mods":[{"id":"private-chests","status":"waiting-for-abi"}]}"#);
    let st = engine_mods::status(&t.0);
    for (kind, plugin, key) in [(ModKind::Enshwick, "kiln-enshwick", "status"), (ModKind::Shroudlink, "kiln-shroudlink", "started"),
                                (ModKind::Ember, "kiln-ember", "mods")] {
        let s = st.iter().find(|s| s.kind == kind).unwrap_or_else(|| panic!("{kind:?}"));
        assert_eq!(s.plugin, plugin);
        assert!(s.state.as_ref().unwrap().get(key).is_some(), "{kind:?}");
    }
    assert!(!engine_mods::runs_yet(ModKind::Ember) && engine_mods::runs_yet(ModKind::Enshwick));
}

#[test]
fn new_kind_imports_refuse_what_their_plugins_cannot_run() {
    let t = tmp("refuse");
    let dirs = DataDirs::at(t.0.join("data"));
    let skipped = |path: PathBuf| {
        let r = library::import(&dirs, &[path.clone()], &mut NoProgress).unwrap();
        assert!(r.imported.is_empty() && r.existing.is_empty() && !r.skipped.is_empty(), "{}: {r:?}", path.display());
        r.skipped[0].reason.clone()
    };
    // Enshwick: the loader's own folder name, and ids the plugin's folder rule or Enshwick refuse
    put(&t.0, "loader/pack.toml", b"id = \"enshwick\"\n");
    assert!(skipped(t.0.join("loader")).contains("loader folder"));
    for (i, id) in ["You.Upper", "../escape", "a b"].iter().enumerate() {
        let d = t.0.join(format!("bad{i}"));
        put(&d, "pack.toml", format!("id = \"{id}\"\n").as_bytes());
        assert!(skipped(d).contains("not a valid mod id"), "{id}");
    }
    // Ember: one library per folder (kiln-ember refuses two), never imported as plain DLLs
    let two = t.0.join("srv/ember/mods/two-libs");
    put(&two, "a.dll", &build_test_dll(&[], b""));
    put(&two, "b.dll", &build_test_dll(&[], b""));
    assert_eq!(library::detect(&two).unwrap().kind, DetectKind::Ember);
    assert!(skipped(two).contains("exactly one library"));
    // the same DLL outside ember/mods/ says nothing about Ember: Kiln's own rules (a plain native DLL)
    let plain = t.0.join("elsewhere/private-chests");
    put(&plain, "private_chests.dll", &build_test_dll(&[], b""));
    assert_ne!(library::detect(&plain).unwrap().kind, DetectKind::Ember);
    // another engine's mod DLL in ember/mods/ is that engine's
    let sl = t.0.join("srv/ember/mods/chestsort");
    put(&sl, "chestsort.dll", &build_test_dll(&["shroudlink_mod_v2"], b""));
    assert_eq!(library::detect(&sl).unwrap().kind, DetectKind::Shroudlink);
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[test]
fn new_kind_links_are_refused() {
    use std::os::unix::fs::symlink;
    let t = tmp("links");
    let dirs = DataDirs::at(t.0.join("data"));
    put(&t.0, "outside.toml", b"[[item]]");
    put(&t.0, "real.dll", &build_test_dll(&["shroudlink_mod_v2"], b""));
    let pack = t.0.join("pack");
    put(&pack, "pack.toml", b"id = \"you.pack\"\n");
    symlink(t.0.join("outside.toml"), pack.join("items.toml")).unwrap();
    let ember = t.0.join("srv/ember/mods/linked");
    std::fs::create_dir_all(&ember).unwrap();
    symlink(t.0.join("real.dll"), ember.join("linked.dll")).unwrap();
    symlink(t.0.join("real.dll"), t.0.join("link.dll")).unwrap();
    let linked_folder = t.0.join("srv/ember/mods/folder-link");
    symlink(t.0.join("pack"), &linked_folder).unwrap();
    for src in [pack, ember, t.0.join("link.dll"), linked_folder] {
        let r = library::import(&dirs, &[src.clone()], &mut NoProgress);
        assert!(r.is_err() || r.unwrap().imported.is_empty(), "{}", src.display());
    }
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[cfg(feature = "server")]
#[test]
fn server_sync_puts_new_kinds_in_each_engines_folder_and_switches_them() {
    use kiln_manage::server::{control, instance::ServerInstance};
    let t = tmp("server");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    // the owner's own Enshwick pack, Enshwick's loader folder, and an Ember mod installed by hand
    put(&server, "mods/owner.pack/pack.toml", b"id = \"owner.pack\"\n");
    put(&server, "mods/enshwick/mods.toml", b"");
    put(&server, "mods/enshwick/enshwick.dll", &build_test_dll(&["enshwick_start"], b"enshwick-loader-stub"));
    put(&server, "ember/mods/own-ember/own.dll", &build_test_dll(&[], b""));
    let inst: ServerInstance = serde_json::from_value(serde_json::json!({
        "id": "t", "name": "t", "kind": "local", "dir": server, "port": 27140, "queryPort": 27140, "wine": null,
        "policy": "any", "source": "launcher", "remote": null })).unwrap();
    let want: Vec<LibraryRef> = [(ModKind::Enshwick, "you.lanterns"), (ModKind::Shroudlink, "chestsort"), (ModKind::Ember, "private-chests")]
        .iter().map(|(k, id)| refs(&r, *k, id)).collect();
    let listed = control::sync(&dirs, &inst, &want, &mut NoProgress).unwrap();
    for (k, rel) in [(ModKind::Enshwick, "mods/you.lanterns"), (ModKind::Shroudlink, "shroudlink_mods/chestsort"),
                     (ModKind::Ember, "ember/mods/private-chests")] {
        assert!(server.join(rel).join(".kiln-source.json").is_file(), "{rel}");
        assert!(listed.iter().any(|m| m.kind == Some(k) && m.source.is_some() && m.enabled), "{k:?} in {listed:?}");
    }
    assert!(server.join("shroudlink_mods/chestsort/chestsort.dll").is_file(), "kiln-shroudlink reads shroudlink_mods/<id>/");
    let lantern = listed.iter().find(|m| m.folder == "you.lanterns").unwrap();
    assert_eq!((lantern.version.as_deref(), lantern.clients), (Some("1.0.0"), Some(Clients::Mod)));
    assert!(listed.iter().any(|m| m.kind == Some(ModKind::Enshwick) && m.folder == "owner.pack" && m.source.is_none()));
    assert!(listed.iter().any(|m| m.kind == Some(ModKind::Ember) && m.folder == "own-ember" && m.source.is_none()));
    assert!(!listed.iter().any(|m| m.folder == "enshwick"), "Enshwick's loader folder is not a mod: {listed:?}");
    // off: out of the engine's folder (Enshwick's own off switch is moving the folder out of mods/); on: back
    for (folder, kind) in [("you.lanterns", "enshwick"), ("private-chests", "ember"), ("chestsort", "shroudlink")] {
        control::set_enabled(&inst, folder, false).unwrap();
        assert!(server.join("kiln/disabled").join(kind).join(folder).is_dir(), "{folder}");
        assert!(control::mods(&inst).unwrap().iter().any(|m| m.folder == folder && !m.enabled));
        control::set_enabled(&inst, folder, true).unwrap();
    }
    assert!(server.join("mods/you.lanterns/pack.toml").is_file());
    // a forged marker in the owner's folder never makes it Kiln's
    let forged = serde_json::json!({ "format": 1, "library": refs(&r, ModKind::Enshwick, "you.greeter"), "synced": "now", "by": "kiln-launcher" });
    put(&server, "mods/you.greeter/pack.toml", b"id = \"you.greeter\"\n");
    put(&server, "mods/you.greeter/.kiln-source.json", serde_json::to_vec(&forged).unwrap().as_slice());
    assert_eq!(control::sync(&dirs, &inst, &[want.clone(), vec![refs(&r, ModKind::Enshwick, "you.greeter")]].concat(), &mut NoProgress)
        .unwrap_err().code, ErrorCode::Conflict);
    // dropped from the list: Kiln's copies go, the owner's stay
    control::sync(&dirs, &inst, &[], &mut NoProgress).unwrap();
    for rel in ["mods/you.lanterns", "shroudlink_mods/chestsort", "ember/mods/private-chests"] {
        assert!(!server.join(rel).exists(), "{rel}");
    }
    for rel in ["mods/owner.pack", "mods/enshwick/enshwick.dll", "ember/mods/own-ember", "mods/you.greeter"] {
        assert!(server.join(rel).exists(), "{rel}");
    }
}

#[test]
fn modpacks_carry_the_enshwick_mods_players_need_and_never_server_only_ones() {
    let t = tmp("modpack");
    let server = t.0.join("server");
    std::fs::create_dir_all(server.join("kiln")).unwrap();
    // players need: an Enshwick pack (side both) and an Enshwick DLL mod (its side is compiled in: both by default)
    put(&server, "mods/you.lanterns/pack.toml", b"id = \"you.lanterns\"\nversion = \"1.0.0\"\nname = \"Lanterns\"\n");
    put(&server, "mods/lockers/lockers.dll", &build_test_dll(&ENSHWICK_EXPORTS, b""));
    // players don't: a server-side Luau mod, Enshwick's loader folder, shroudlink and Ember mods
    put(&server, "mods/you.greeter/pack.toml", b"id = \"you.greeter\"\nside = \"server\"\n");
    put(&server, "mods/you.greeter/main.luau", b"");
    put(&server, "mods/enshwick/enshwick.dll", &build_test_dll(&["enshwick_start"], b"enshwick"));
    put(&server, "shroudlink_mods/chestsort/chestsort.dll", &build_test_dll(&["shroudlink_mod_v2"], b""));
    put(&server, "ember/mods/private-chests/private_chests.dll", &build_test_dll(&[], b""));
    put(&server, "ember/mods/private-chests/mod.json", br#"{"clients":"mod","side":"both"}"#);
    let need = modpack::client_mods(&server).unwrap();
    assert_eq!(need.iter().map(|m| (m.kind, m.id.as_str())).collect::<Vec<_>>(), [(ModKind::Enshwick, "lockers"), (ModKind::Enshwick, "you.lanterns")]);
    let req = modpack::required(&server).unwrap();
    assert_eq!(req[1].hash, hash::dirhash(&server.join("mods/you.lanterns")).unwrap());
    assert_eq!(req[1].version, "1.0.0");
    let built = modpack::build(&server, &t.0.join("out"), "Isles", "1", &mut NoProgress).unwrap();
    assert_eq!(built.manifest.mods.iter().map(|m| m.path.as_str()).collect::<Vec<_>>(), ["enshwick/lockers", "enshwick/you.lanterns"]);
    // a player installs it: the library takes both with the same kdh1, the profile lists them
    let dirs = DataDirs::at(t.0.join("data"));
    let game = game_with_z(&t.0);
    let res = modpack::install(&dirs, &game, &modpack::ModpackInstallRequest {
        source: modpack::ModpackSource::Path { path: built.file.clone(), sha256: Some(built.sha256.clone()) },
        consent: true, allow_http: false, activate: Some(false),
    }, &mut NoProgress).unwrap();
    let mut got: Vec<_> = res.profile.mods.enshwick.iter().map(|m| (m.id.clone(), m.hash.clone())).collect();
    got.sort();
    assert_eq!(got, req.iter().map(|m| (m.id.clone(), m.hash.clone())).collect::<Vec<_>>());
}

#[test]
fn enshwick_metadata_is_bounded_unambiguous_and_authoritative() {
    let t = tmp("pack-adversarial");
    let dirs = DataDirs::at(t.0.join("data"));
    let invalid = [
        "id = 'safe'\nid = 'enshwick'\n",
        "id = 'safe'\nside = 'server'\nside = 'both'\n",
        "id = 'safe'\nside = 1\n",
        "\u{feff}\u{feff}id = 'safe'\n",
        "id = 'safe'\nversion = 'not-a-version'\n",
        "id = 'safe'\nversion = '1.0.0-01'\n",
        "id = 'safe'\nversion = '01.0.0'\n",
        "id = 'safe'\nversion = ''\n",
        "id = 'safe'\nversion = '1'\n",
        "id = 'safe'\nname = \"bad\\q\"\n",
        "id = 'safe'\nname = \"bad\\uD800\"\n",
        "id = 'safe'\nname = \"bad\\U00110000\"\n",
        "id = 'safe'\nname = \"open\n",
        "id = 'safe'\nside = \"server\\n\"\n",
        "id = 'safe'\nauthors = [\n# ]\nid = 'enshwick'\n]\n",
        "id = 'safe'\nx = { name = 'x',\nid = 'enshwick'\n}\n",
        "id = 'safe'\nx = [\"[\"]\n",
    ];
    for (i, text) in invalid.iter().enumerate() {
        let folder = t.0.join(format!("invalid{i}"));
        put(&folder, "pack.toml", text.as_bytes());
        assert_eq!(engine_mods::identity(&folder).0, "", "invalid metadata has no fallback identity");
        let r = library::import(&dirs, &[folder], &mut NoProgress).unwrap();
        assert!(r.imported.is_empty() && r.existing.is_empty() && !r.skipped.is_empty(), "{text:?}: {r:?}");
    }
    let huge = t.0.join("huge");
    put(&huge, "pack.toml", format!("id = 'huge'\n#{}", "x".repeat(1024 * 1024)).as_bytes());
    assert!(kiln_manage::engines::pack_toml(&huge).is_none());
    let deep = t.0.join("deep");
    put(&deep, "pack.toml", format!("id = 'deep'\nx = {}", "[".repeat(100_000)).as_bytes());
    assert!(kiln_manage::engines::pack_toml(&deep).is_none());
    let bad_utf8 = t.0.join("utf8");
    put(&bad_utf8, "pack.toml", b"id = 'safe'\n# \xff");
    assert!(kiln_manage::engines::pack_toml(&bad_utf8).is_none());
    assert!(library::list(&dirs, None).unwrap().is_empty());

    let valid = t.0.join("valid");
    put(&valid, "pack.toml", b"id = \"\"\"you.\\\n  pack\"\"\"\nversion = '''1.2.3'''\nname = \"Unicode \\U0001F525\"\nside = '''server'''\n[ requires . foo ]\nid = 'ignored'\n");
    // mod.json belongs to a different framework and cannot weaken Enshwick's join requirement.
    put(&valid, "mod.json", br#"{"id":"other","side":"both","clients":"mod"}"#);
    assert_eq!(engine_mods::identity(&valid).0, "you.pack");
    let r = library::import(&dirs, &[valid], &mut NoProgress).unwrap();
    let x = refs(&r, ModKind::Enshwick, "you.pack");
    assert_eq!(x.version, "1.2.3");
    let rec = library::record(&dirs, &x).unwrap();
    assert_eq!((rec.side, rec.clients), (Some(Side::Server), Some(Clients::None)));
    let both = t.0.join("both");
    put(&both, "pack.toml", b"id = 'you.both'\nside = 'both'\n");
    put(&both, "mod.json", br#"{"side":"server","clients":"none"}"#);
    let r = library::import(&dirs, &[both], &mut NoProgress).unwrap();
    let rec = library::record(&dirs, &refs(&r, ModKind::Enshwick, "you.both")).unwrap();
    assert_eq!((rec.side, rec.clients), (Some(Side::Both), Some(Clients::Mod)));
}

#[test]
fn enshwick_ids_must_be_safe_for_the_windows_loader_folder() {
    let t = tmp("windows-ids");
    let dirs = DataDirs::at(t.0.join("data"));
    for (i, id) in ["enshwick.", "enshwick..", "con", "nul.mod", "aux", "com1", "lpt9.mod", "trail.", "Enshwick", "enshwiсk"].iter().enumerate() {
        let folder = t.0.join(format!("bad{i}"));
        put(&folder, "pack.toml", format!("id = '{id}'\n").as_bytes());
        let r = library::import(&dirs, &[folder], &mut NoProgress).unwrap();
        assert!(r.imported.is_empty() && r.existing.is_empty() && !r.skipped.is_empty(), "{id}: {r:?}");
    }
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[cfg(feature = "server")]
#[test]
fn new_engine_sync_preserves_hand_installed_and_modified_folders_and_stages_replacements() {
    use kiln_manage::server::{control, instance::ServerInstance};
    let t = tmp("ownership");
    let dirs = DataDirs::at(t.0.join("data"));
    let imported = library::import(&dirs, &sources(&t.0), &mut NoProgress).unwrap();
    let inst = |dir: &Path| -> ServerInstance {
        std::fs::create_dir_all(dir.join("kiln")).unwrap();
        serde_json::from_value(serde_json::json!({"id":"t","name":"t","kind":"local","dir":dir,
            "port":27140,"queryPort":27140,"wine":null,"policy":"any","source":"launcher","remote":null})).unwrap()
    };
    for (kind, id, rel) in [
        (ModKind::Enshwick, "you.lanterns", "mods/you.lanterns"),
        (ModKind::Shroudlink, "chestsort", "shroudlink_mods/chestsort"),
        (ModKind::Ember, "private-chests", "ember/mods/private-chests"),
    ] {
        let x = refs(&imported, kind, id);
        let own_server = t.0.join(format!("own-{}", kind.as_str()));
        let own = inst(&own_server);
        let dst = own_server.join(rel);
        put(&dst, "owner.txt", b"owner data");
        // A matching source marker is only a claim; an independently stored receipt is required.
        put(&dst, ".kiln-source.json", serde_json::to_vec(&serde_json::json!({
            "format":1,"library":x,"synced":"now","by":"kiln-launcher"})).unwrap().as_slice());
        assert_eq!(control::sync(&dirs, &own, &[x.clone()], &mut NoProgress).unwrap_err().code, ErrorCode::Conflict);
        control::sync(&dirs, &own, &[], &mut NoProgress).unwrap();
        assert_eq!(std::fs::read(dst.join("owner.txt")).unwrap(), b"owner data");

        let server = t.0.join(format!("managed-{}", kind.as_str()));
        let managed = inst(&server);
        control::sync(&dirs, &managed, &[x.clone()], &mut NoProgress).unwrap();
        let dst = server.join(rel);
        let original = hash::dirhash(&dst).unwrap();
        // A corrupt replacement must fail validation before the installed payload is changed.
        let replacement_source = if kind == ModKind::Ember {
            t.0.join("replacement/ember/mods").join(id)
        } else { t.0.join("replacement").join(id) };
        std::fs::create_dir_all(&replacement_source).unwrap();
        for e in std::fs::read_dir(library::path_of(&dirs, &x).unwrap()).unwrap() {
            let e = e.unwrap();
            if e.file_type().unwrap().is_file() && !e.file_name().to_string_lossy().starts_with('.') {
                std::fs::copy(e.path(), replacement_source.join(e.file_name())).unwrap();
            }
        }
        put(&replacement_source, "new-payload.txt", b"new version");
        let replacement = library::import(&dirs, &[replacement_source], &mut NoProgress).unwrap();
        let replacement = refs(&replacement, kind, id);
        assert_ne!(replacement.hash, x.hash);
        let payload = library::path_of(&dirs, &replacement).unwrap().join("new-payload.txt");
        std::fs::write(&payload, b"corrupted after import").unwrap();
        assert!(control::sync(&dirs, &managed, &[replacement], &mut NoProgress).is_err());
        assert_eq!(hash::dirhash(&dst).unwrap(), original);
        std::fs::remove_file(payload).unwrap();
        // Changes made by the owner revoke deletion and replacement authority.
        put(&dst, "local-settings.txt", b"keep settings");
        control::sync(&dirs, &managed, &[], &mut NoProgress).unwrap();
        assert_eq!(std::fs::read(dst.join("local-settings.txt")).unwrap(), b"keep settings");
        assert_eq!(control::sync(&dirs, &managed, &[x], &mut NoProgress).unwrap_err().code, ErrorCode::Conflict);
    }
}

#[test]
fn ancillary_pack_does_not_override_another_engines_manifest() {
    let t = tmp("pack-precedence");
    for (label, kind) in [("flame", DetectKind::Flame), ("sf", DetectKind::ShroudForge)] {
        let folder = t.0.join(label);
        let manifest = if kind == DetectKind::Flame {
            br#"{"id":"you.flame","name":"Flame name","version":"1.0.0"}"#.as_slice()
        } else {
            br#"{"id":"you.sf","name":"SF name","version":"1.0.0","capabilities":{"runtime":true},"$schema":"shroudforge.schema.json","targets":["client"]}"#.as_slice()
        };
        put(&folder, "mod.json", manifest);
        if kind == DetectKind::Flame { put(&folder, "flame.json", b"{}"); }
        // Both valid and invalid ancillary pack metadata retain framework precedence.
        for pack in ["id = 'you.pack'\nversion = '2.0.0'\n", "id = 'enshwick'\nid = 'duplicate'\n"] {
            put(&folder, "pack.toml", pack.as_bytes());
            assert_eq!(library::detect(&folder).unwrap().kind, kind);
            let id = format!("you.{label}");
            assert_eq!(engine_mods::identity(&folder).0, id);
            assert_eq!(engine_mods::manifest_json(&folder).unwrap()["id"], id);
        }
    }
}
