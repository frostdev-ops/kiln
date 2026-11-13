//! A mod changed in its own Library folder (Nexus report of 2026-11-13: a player used "Open folder" on an EML mod and
//! translated its mod.json's name). The list notices it, Play and activation say which mod was changed and carry the
//! entry to take over, and `library::take_over` makes the changed files the mod's new entry: new hash, new record from
//! the manifest, profiles moved to it, the move kept. Every kind the Library holds.
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
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
    let p = std::env::temp_dir().join(format!("km-library-changed-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

fn game_with_z(t: &Path) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    GameInstall {
        id: "g-changed".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into(),
    }
}

fn no_game(_: &Path, _: &str) -> kiln_manage::Result<Option<kiln_manage::proc::ProcessInfo>> {
    Ok(None)
}

/// The record of a Library entry written a while ago (what an import some time before the edit leaves): the folder's
/// files edited now are newer than it.
fn age_record(folder: &Path) {
    let rec = PathBuf::from(format!("{}.json", folder.display()));
    let f = std::fs::File::options().write(true).open(&rec).unwrap();
    f.set_modified(std::time::SystemTime::now() - std::time::Duration::from_secs(120)).unwrap();
}

fn version_of<'a>(list: &'a [library::LibraryEntry], kind: ModKind, id: &str) -> &'a library::LibraryEntry {
    list.iter().find(|e| e.kind == kind && e.id == id).unwrap_or_else(|| panic!("{kind:?} {id} not listed: {list:?}"))
}

/// The player's steps: an XHL package (EML's loader, an EML mod that is an XHL-Core plugin too, placed in the game's
/// mods folder) dropped on the launcher; its mod.json edited in the Library folder ("Open folder"), its Chinese name
/// put into English. Before: the list kept the old name, and the next placement refused with "<path>: library hash
/// mismatch" and nothing to do but import the mod again.
#[test]
fn a_mod_json_edited_in_its_library_folder_is_taken_over_with_one_action() {
    let t = tmp("player");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    let pkg = t.0.join("pkg/XHL Grappling Hook Pull 2x");
    put(&pkg, "dbghelp.dll", &build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH"));
    let old_json = r#"{"id":"xhl-grappling-hook-pull-2x","name":"钩爪拉力2倍","version":"2.0.0","capabilities":["patch"]}"#;
    put(&pkg, "mods/xhl-grappling-hook-pull-2x/mod.json", old_json.as_bytes());
    put(&pkg, "mods/xhl-grappling-hook-pull-2x/src/mod.lua", b"return {}");
    put(&pkg, "mods/xhl-grappling-hook-pull-2x/xhl-grappling-hook-pull-2x.dll", &build_test_dll(&["XhlPluginEntry"], b"XHL plugin"));
    put(&pkg, "mods/xhl-grappling-hook-pull-2x/native-plugin.ini", b"[Plugin]\nDll=xhl-grappling-hook-pull-2x.dll\n");
    let added = kiln_manage::quick::add_with(&dirs, &g, &[pkg], false, &mut NoProgress).unwrap();
    let old = added.import.imported.iter().find(|r| r.kind == ModKind::Eml).cloned().expect("the EML mod");
    let entry = library::path_of(&dirs, &old).unwrap();
    let copy = g.path.join("mods/xhl-grappling-hook-pull-2x");
    assert!(copy.join("mod.json").is_file(), "placed where XHL-Core finds it");
    let lib = library::list(&dirs, None).unwrap();
    assert!(!version_of(&lib, ModKind::Eml, &old.id).versions[0].changed, "nothing changed yet");

    // "Open folder", mod.json edited: the name in English
    age_record(&entry);
    let new_json = old_json.replace("钩爪拉力2倍", "Grappling Hook Pull 2x");
    put(&entry, "mod.json", new_json.as_bytes());

    // the list notices it (one entry hashed: its manifest is newer than its record) and still shows what was imported
    let lib = library::list(&dirs, None).unwrap();
    let e = version_of(&lib, ModKind::Eml, &old.id);
    assert!(e.versions[0].changed, "Changed on disk: {e:?}");
    assert_eq!(e.name, "钩爪拉力2倍");

    // Play after the mod was switched off and on again (the profile changed, so Play writes it again and places the
    // entry): it names the mod, without "the active profile changed and cannot be applied:" in front (a mod's files
    // changed, not the profile)
    let start = kiln_manage::launch::LaunchRequest { mode: kiln_manage::launch::LaunchMode::Profile, profile_id: None, skip_backup: true };
    let switch = |on: bool| {
        let mut p = profile::get(&dirs, "my-mods").unwrap();
        p.mods.eml[0].enabled = on;
        profile::save(&dirs, &p).unwrap();
    };
    switch(false);
    kiln_manage::launch::play_with(&dirs, &g, &start, Some(&[]), &|_: &str| Ok(())).unwrap();
    switch(true);
    let play = kiln_manage::launch::play_with(&dirs, &g, &start, Some(&[]), &|_: &str| Ok(())).unwrap_err();
    assert!(play.message.starts_with("钩爪拉力2倍 was changed in its folder ("), "{}", play.message);

    // a placement of the entry (here: the profile loaded again) says which mod was changed, and how to go on
    profile::deactivate(&dirs, &g).unwrap();
    let err = profile::activate(&dirs, "my-mods", &g).unwrap_err();
    assert_eq!(err.code, ErrorCode::HashMismatch);
    assert!(err.message.starts_with("钩爪拉力2倍 was changed in its folder ("), "{}", err.message);
    assert!(err.message.contains("Use the changed files"), "{}", err.message);
    assert!(!err.message.contains("library hash mismatch"), "{}", err.message);
    let detail = err.detail.expect("detail");
    assert_eq!(serde_json::from_value::<LibraryRef>(detail["libraryChanged"].clone()).unwrap(), old, "the entry to take over");

    // "Use the changed files"
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert_eq!(done.from, old);
    assert_eq!(done.name, "Grappling Hook Pull 2x");
    let new = done.to.clone();
    assert_eq!((new.kind, new.id.as_str(), new.version.as_str()), (ModKind::Eml, old.id.as_str(), "2.0.0"));
    assert_ne!(new.hash, old.hash);
    assert_eq!(new.hash, kiln_manage::hash::dirhash(&library::path_of(&dirs, &new).unwrap()).unwrap());
    // the list: one version, the new name, nothing changed
    let lib = library::list(&dirs, None).unwrap();
    let e = version_of(&lib, ModKind::Eml, &old.id);
    assert_eq!((e.name.as_str(), e.versions.len()), ("Grappling Hook Pull 2x", 1), "{e:?}");
    assert_eq!(e.versions[0].hash, new.hash);
    assert!(!e.versions[0].changed);
    let rec = library::record(&dirs, &new).unwrap();
    assert_eq!(rec.game_folder.as_deref(), Some("xhl-grappling-hook-pull-2x"), "what Kiln knew of the package stays");
    assert!(!entry.exists(), "the old entry is gone: nothing reads it any more");
    assert_eq!(library::moved_to(&dirs, &old), old, "a take-over is a change of contents, not a move: a request by the old hash is never handed the new one");
    // the profile lists the new entry, and the game gets the changed files
    let p = profile::get(&dirs, "my-mods").unwrap();
    assert_eq!(p.mods.eml.iter().map(|m| (m.version.as_str(), m.hash.as_str())).collect::<Vec<_>>(), [("2.0.0", new.hash.as_str())]);
    profile::activate(&dirs, "my-mods", &g).unwrap();
    assert_eq!(std::fs::read_to_string(copy.join("mod.json")).unwrap(), new_json);
    // again: nothing left to take over
    let again = library::take_over_with(&dirs, &new, &mut no_game).unwrap();
    assert_eq!(again.to, new);
}

/// While a game that may read the entry runs, nothing is moved: the refusal says why.
#[test]
fn a_take_over_is_refused_while_the_game_runs() {
    let t = tmp("running");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    kiln_manage::game::write_record(&dirs, &g, None).unwrap();
    let src = t.0.join("src/stacks");
    put(&src, "mod.json", br#"{"id":"stacks","name":"Stacks","version":"1.0.0","capabilities":["patch"]}"#);
    put(&src, "src/mod.lua", b"return {}");
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    let old = r.imported[0].clone();
    let entry = library::path_of(&dirs, &old).unwrap();
    put(&entry, "mod.json", br#"{"id":"stacks","name":"Bigger Stacks","version":"1.0.0","capabilities":["patch"]}"#);
    let game = g.path.clone();
    let mut running = |d: &Path, e: &str| Ok((d == game && e == "enshrouded.exe").then(|| kiln_manage::proc::ProcessInfo { pid: 4242, name: e.into(), exe: None, cmdline: vec![] }));
    let err = library::take_over_with(&dirs, &old, &mut running).unwrap_err();
    assert_eq!(err.code, ErrorCode::GameRunning);
    assert!(err.message.contains("Close Enshrouded first"), "{}", err.message);
    assert!(entry.is_dir() && library::record(&dirs, &old).is_ok() && library::moved(&dirs).is_empty(), "nothing moved");
}

/// An id changed in the manifest makes another mod: refused, with what to do. A folder that is no longer a mod of its
/// kind: refused, with the reason.
#[test]
fn a_changed_id_or_an_unreadable_folder_is_not_taken_over() {
    let t = tmp("refused");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/stacks");
    put(&src, "mod.json", br#"{"id":"stacks","name":"Stacks","version":"1.0.0","capabilities":["patch"]}"#);
    put(&src, "src/mod.lua", b"return {}");
    let old = library::import(&dirs, &[src], &mut NoProgress).unwrap().imported[0].clone();
    let entry = library::path_of(&dirs, &old).unwrap();
    put(&entry, "mod.json", br#"{"id":"other","name":"Stacks","version":"1.0.0","capabilities":["patch"]}"#);
    let err = library::take_over_with(&dirs, &old, &mut no_game).unwrap_err();
    assert_eq!(err.code, ErrorCode::Refused);
    assert!(err.message.contains("\"other\" instead of \"stacks\""), "{}", err.message);
    std::fs::remove_file(entry.join("src/mod.lua")).unwrap();
    std::fs::remove_file(entry.join("mod.json")).unwrap();
    let err = library::take_over_with(&dirs, &old, &mut no_game).unwrap_err();
    assert_eq!(err.code, ErrorCode::Refused);
    assert!(err.message.contains("cannot read it as a mod of its kind"), "{}", err.message);
    assert!(library::record(&dirs, &old).is_ok(), "nothing moved");
}

/// Every kind the Library holds: its manifest edited in the Library folder (a new name, and for most a new version),
/// noticed by the list, taken over, listed by the profile under its new entry.
#[test]
fn every_kind_is_taken_over() {
    let t = tmp("kinds");
    let dirs = DataDirs::at(t.0.join("data"));
    let s = t.0.join("src");
    // (kind, id, folder, manifest file, old text, new text, name after, version after)
    let kiln_old = r#"{ "id": "blades", "name": "Blades", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#;
    put(&s.join("blades"), "kiln.mod.json", kiln_old.as_bytes());
    put(&s.join("blades"), "M.dll", b"blades");
    let native_old = r#"{ "format": 1, "id": "lamp", "name": "Lamp", "version": "1.0.0", "dlls": ["lamp.dll"], "phase": "early", "init": "kiln_native_init" }"#;
    put(&s.join("lamp"), "native.json", native_old.as_bytes());
    put(&s.join("lamp"), "lamp.dll", &build_test_dll(&["kiln_native_init"], b"lamp"));
    let emf_old = r#"{"id":"emfmod","name":"EMF Mod","version":"1.0.0","emf":21,"dll":"emfmod.dll"}"#;
    put(&s.join("emfmod"), "mod.json", emf_old.as_bytes());
    put(&s.join("emfmod"), "emfmod.dll", &build_test_dll(&["EmfModInit"], b"emf"));
    let eml_old = r#"{"id":"stacks","name":"Stacks","version":"1.2.0","capabilities":["patch"]}"#;
    put(&s.join("stacks"), "mod.json", eml_old.as_bytes());
    put(&s.join("stacks"), "src/mod.lua", b"return {}");
    let sf_old = r#"{"id":"sfpack","name":"SF Pack","version":"1.0.0"}"#;
    put(&s.join("sfpack"), "mod.json", sf_old.as_bytes());
    put(&s.join("sfpack"), "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false}"#);
    put(&s.join("sfpack"), "src/mod.lua", b"return {}");
    let set_old = r#"{"id":"flymode","name":"Fly Mode","version":"1.0.0"}"#;
    put(&s.join("flymode"), "mod.json", set_old.as_bytes());
    put(&s.join("flymode"), "init.lua", b"print('fly')");
    let ge_old = r#"{"id":"worstmap","name":"The Worst Map Mod","version":"1.0.0","dll":"map.dll","side":"client"}"#;
    put(&s.join("worstmap"), "mod.json", ge_old.as_bytes());
    put(&s.join("worstmap"), "map.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    let flame_old = r#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#;
    put(&s.join("creative"), "mod.json", flame_old.as_bytes());
    put(&s.join("creative"), "flame.json", br#"{"dll":"creative.dll"}"#);
    put(&s.join("creative"), "creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    let r = library::import(&dirs, &std::fs::read_dir(&s).unwrap().map(|e| e.unwrap().path()).collect::<Vec<_>>(), &mut NoProgress).unwrap();
    assert!(r.skipped.is_empty(), "{:?}", r.skipped);
    let cases: [(ModKind, &str, &str, &str, &str); 8] = [
        (ModKind::Kiln, "blades", "kiln.mod.json", kiln_old, "Blades"),
        (ModKind::Native, "lamp", "native.json", native_old, "Lamp"),
        (ModKind::Emf, "emfmod", "mod.json", emf_old, "EMF Mod"),
        (ModKind::Eml, "stacks", "mod.json", eml_old, "Stacks"),
        (ModKind::ShroudForge, "sfpack", "mod.json", sf_old, "SF Pack"),
        (ModKind::Set, "flymode", "mod.json", set_old, "Fly Mode"),
        (ModKind::Greeneye, "worstmap", "mod.json", ge_old, "The Worst Map Mod"),
        (ModKind::Flame, "creative", "mod.json", flame_old, "Creative Mode"),
    ];
    let mut p = profile::create(&dirs, "every kind", None).unwrap();
    let mut olds = Vec::new();
    for (kind, id, ..) in cases {
        let old = r.imported.iter().find(|x| x.kind == kind && x.id == id).cloned().unwrap_or_else(|| panic!("{kind:?} {id}: {r:?}"));
        p.mods.list_mut(kind).push(ProfileMod { id: id.into(), version: old.version.clone(), hash: old.hash.clone(), enabled: true });
        olds.push(old);
    }
    let p = profile::save(&dirs, &p).unwrap();
    for ((kind, id, file, text, name), old) in cases.iter().zip(&olds) {
        let entry = library::path_of(&dirs, old).unwrap();
        age_record(&entry);
        let (from_v, to_v) = if *kind == ModKind::Flame { ("1.4.6", "1.4.7") } else if *kind == ModKind::Eml { ("1.2.0", "1.3.0") } else { ("1.0.0", "1.1.0") };
        let edited = text.replace(name, &format!("{name} (edited)")).replace(from_v, to_v);
        put(&entry, file, edited.as_bytes());
        let lib = library::list(&dirs, Some(*kind)).unwrap();
        assert!(version_of(&lib, *kind, id).versions[0].changed, "{kind:?}: noticed");
        let done = library::take_over_with(&dirs, old, &mut no_game).unwrap_or_else(|e| panic!("{kind:?}: {e:?}"));
        assert_eq!((done.name.clone(), done.to.version.as_str()), (format!("{name} (edited)"), to_v), "{kind:?}");
        assert_ne!(done.to.hash, old.hash, "{kind:?}");
        let lib = library::list(&dirs, Some(*kind)).unwrap();
        let e = version_of(&lib, *kind, id);
        assert_eq!((e.name.clone(), e.versions.len(), e.versions[0].changed), (format!("{name} (edited)"), 1, false), "{kind:?}: {e:?}");
        assert_eq!(library::moved_to(&dirs, old), *old, "{kind:?}: not remapped");
    }
    let p2 = profile::get(&dirs, &p.id).unwrap();
    for (kind, id, ..) in cases {
        let m = &p2.mods.list(kind)[0];
        assert_eq!(m.id, id);
        assert_eq!(library::path_of(&dirs, &LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() }).unwrap(),
            version_of(&library::list(&dirs, Some(kind)).unwrap(), kind, id).versions[0].folder, "{kind:?}: the profile lists the new entry");
    }
    // the profile still activates with every changed mod
    let g = game_with_z(&t.0);
    profile::activate(&dirs, &p.id, &g).unwrap();
    assert!(std::fs::read_to_string(g.path.join("shrouded_engine_tweaks/mods/flymode/mod.json")).unwrap().contains("Fly Mode (edited)"));
}

/// A plugin DLL replaced in its Library folder: taken over by its sha256.
#[test]
fn a_replaced_plugin_dll_is_taken_over() {
    let t = tmp("plugin");
    let dirs = DataDirs::at(t.0.join("data"));
    put(&t.0, "src/hello.dll", &build_test_dll(&["kiln_plugin_main"], b"v1"));
    let r = library::import(&dirs, &[t.0.join("src/hello.dll")], &mut NoProgress).unwrap();
    let old = r.imported.iter().find(|x| x.kind == ModKind::Plugin).cloned().unwrap_or_else(|| panic!("{r:?}"));
    let entry = library::path_of(&dirs, &old).unwrap();
    age_record(entry.parent().unwrap());
    put(entry.parent().unwrap(), "hello.dll", &build_test_dll(&["kiln_plugin_main"], b"v2"));
    assert!(version_of(&library::list(&dirs, Some(ModKind::Plugin)).unwrap(), ModKind::Plugin, &old.id).versions[0].changed);
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert_eq!(done.to.hash, kiln_manage::hash::sha256_file(&library::path_of(&dirs, &done.to).unwrap()).unwrap().0);
    assert!(!version_of(&library::list(&dirs, Some(ModKind::Plugin)).unwrap(), ModKind::Plugin, &old.id).versions[0].changed);
}

/// Copy mode: a Kiln mod whose kiln.mod.json was edited in its Library folder is refused by name, with the entry to take
/// over, instead of "the copy has <hash>, the library entry <hash>".
#[test]
fn copy_mode_names_the_changed_kiln_mod() {
    let t = tmp("copy-mode");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src/blades");
    put(&src, "kiln.mod.json", br#"{ "id": "blades", "name": "Blades", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#);
    put(&src, "M.dll", b"blades");
    let old = library::import(&dirs, &[src], &mut NoProgress).unwrap().imported[0].clone();
    let mut p = profile::create(&dirs, "p", None).unwrap();
    p.mods.kiln.push(ProfileMod { id: old.id.clone(), version: old.version.clone(), hash: old.hash.clone(), enabled: true });
    let p = profile::save(&dirs, &p).unwrap();
    put(&library::path_of(&dirs, &old).unwrap(), "kiln.mod.json",
        br#"{ "id": "blades", "name": "Sharp Blades", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#);
    let mut g = game_with_z(&t.0);
    g.path_mode = PathMode::Copy;
    g.prefix = None;
    let err = profile::activate(&dirs, &p.id, &g).unwrap_err();
    assert_eq!(err.code, ErrorCode::HashMismatch);
    assert!(err.message.starts_with("Blades was changed in its folder ("), "{}", err.message);
    assert_eq!(serde_json::from_value::<LibraryRef>(err.detail.unwrap()["libraryChanged"].clone()).unwrap(), old);
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert_eq!(done.name, "Sharp Blades");
    profile::activate(&dirs, &p.id, &g).unwrap();
    let f = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(f.mods.kiln[0].hash.as_deref(), Some(done.to.hash.as_str()));
    let copy = g.path.join("kiln").join(&f.mods.kiln[0].path);
    assert!(std::fs::read_to_string(copy.join("kiln.mod.json")).unwrap().contains("Sharp Blades"), "the game gets the changed files");
}

// ---- review of 2026-11-13 (a take-over moves and deletes a player's mod folders) ----

fn eml_src(root: &Path, dir: &str, version: &str, body: &[u8]) -> PathBuf {
    let d = root.join(dir);
    put(&d, "mod.json", format!(r#"{{"id":"stacks","name":"Stacks","version":"{version}","capabilities":["patch"]}}"#).as_bytes());
    put(&d, "src/mod.lua", body);
    d
}

fn copy_over(from: &Path, to: &Path) {
    std::fs::remove_dir_all(to).unwrap();
    kiln_manage::util::copy_tree(from, to).unwrap();
}

/// 1. A destination entry whose own files the player changed is never deleted to make room: versions A and B are in the
/// Library, B is edited, then A's folder gets B's originally imported files; taking A over lands on B's entry, which is
/// not exactly those files. Refused, and B keeps its edit.
#[test]
fn a_take_over_never_deletes_another_entrys_edits() {
    let t = tmp("review-dest");
    let dirs = DataDirs::at(t.0.join("data"));
    let a_src = eml_src(&t.0, "src/a", "1.0.0", b"return 'a'");
    let b_src = eml_src(&t.0, "src/b", "1.1.0", b"return 'b'");
    let r = library::import(&dirs, &[a_src, b_src.clone()], &mut NoProgress).unwrap();
    let a = r.imported.iter().find(|x| x.version == "1.0.0").unwrap().clone();
    let b = r.imported.iter().find(|x| x.version == "1.1.0").unwrap().clone();
    let b_dir = library::path_of(&dirs, &b).unwrap();
    put(&b_dir, "src/edit.lua", b"the player's own edit of B");
    copy_over(&b_src, &library::path_of(&dirs, &a).unwrap());
    let err = library::take_over_with(&dirs, &a, &mut no_game).unwrap_err();
    assert_eq!(err.code, ErrorCode::Conflict, "{err:?}");
    assert!(err.message.contains("take that one over first"), "{}", err.message);
    assert_eq!(std::fs::read(b_dir.join("src/edit.lua")).unwrap(), b"the player's own edit of B", "B's edit is kept");
    assert!(library::record(&dirs, &a).is_ok() && library::path_of(&dirs, &a).unwrap().is_dir(), "A is left as it is");
    // B taken over first, then A: A's files are B's original ones, a new entry of their own again
    library::take_over_with(&dirs, &b, &mut no_game).unwrap();
    let done = library::take_over_with(&dirs, &a, &mut no_game).unwrap();
    assert_eq!(done.to, b, "A's files are exactly B's imported entry, which is free again");
}

/// 2. The folder changed again while it was taken over (an editor saved once more): it is not deleted, the newer edit
/// stays in it, and the answer says so.
#[test]
fn a_folder_changed_again_during_the_take_over_is_kept() {
    let t = tmp("review-again");
    let dirs = DataDirs::at(t.0.join("data"));
    let old = library::import(&dirs, &[eml_src(&t.0, "src/a", "1.0.0", b"return 1")], &mut NoProgress).unwrap().imported[0].clone();
    let folder = library::path_of(&dirs, &old).unwrap();
    put(&folder, "src/mod.lua", b"return 2");
    kiln_manage::game::write_record(&dirs, &game_with_z(&t.0), None).unwrap();
    // the process lookup runs at the start and again right before the old folder goes (each time twice for the one known
    // game: its client and its server exe): the editor saves between the two
    let mut calls = 0;
    let f2 = folder.clone();
    let mut find = |_: &Path, _: &str| {
        calls += 1;
        if calls == 3 { put(&f2, "src/mod.lua", b"return 3"); }
        Ok(None)
    };
    let done = library::take_over_with(&dirs, &old, &mut find).unwrap();
    assert!(calls >= 3);
    assert_eq!(std::fs::read(folder.join("src/mod.lua")).unwrap(), b"return 3", "the newer edit is still there");
    assert!(done.note.as_deref().is_some_and(|n| n.contains("changed again")), "{done:?}");
    assert_ne!(done.to, old);
    // and it reads as changed again, to be taken over once more
    assert!(library::list(&dirs, None).unwrap().iter().flat_map(|e| e.versions.iter()).any(|v| v.hash == old.hash));
}

/// 4. A take-over stopped after its new entry was made (a profile could not be saved) is finished by trying again.
#[test]
fn a_take_over_stopped_half_way_is_finished_by_trying_again() {
    use std::os::unix::fs::PermissionsExt;
    let t = tmp("review-retry");
    let dirs = DataDirs::at(t.0.join("data"));
    let old = library::import(&dirs, &[eml_src(&t.0, "src/a", "1.0.0", b"return 1")], &mut NoProgress).unwrap().imported[0].clone();
    let mut ids = Vec::new();
    for name in ["one", "two"] {
        let mut p = profile::create(&dirs, name, None).unwrap();
        p.mods.eml.push(ProfileMod { id: old.id.clone(), version: old.version.clone(), hash: old.hash.clone(), enabled: true });
        ids.push(profile::save(&dirs, &p).unwrap().id);
    }
    put(&library::path_of(&dirs, &old).unwrap(), "src/mod.lua", b"return 2");
    let locked = dirs.profile(&ids[1]);
    std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o555)).unwrap();
    let first = library::take_over_with(&dirs, &old, &mut no_game);
    std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o755)).unwrap();
    assert!(first.is_err(), "{first:?}");
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert_ne!(done.to, old);
    for id in &ids {
        assert_eq!(profile::get(&dirs, id).unwrap().mods.eml[0].hash, done.to.hash, "{id}: finished");
    }
    assert!(library::record(&dirs, &old).is_err(), "the old entry went");
}

/// 3 and 5. A take-over hands nobody another hash for the one they asked for, and leaves nothing that hides an entry
/// later: the original imported again is listed, and survives the migration; editing back and taking over again too.
#[test]
fn a_take_over_leaves_no_record_that_remaps_or_hides() {
    let t = tmp("review-history");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = eml_src(&t.0, "src/a", "1.0.0", b"return 1");
    let a = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap().imported[0].clone();
    let folder = library::path_of(&dirs, &a).unwrap();
    put(&folder, "src/mod.lua", b"return 2");
    let b = library::take_over_with(&dirs, &a, &mut no_game).unwrap().to;
    assert_eq!(library::moved_to(&dirs, &a), a, "a request for A (a server's list) is not handed B");
    let again = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    assert_eq!(again.imported, [a.clone()], "{again:?}");
    let hashes = |dirs: &DataDirs| library::list(dirs, None).unwrap().iter().flat_map(|e| e.versions.iter().map(|v| v.hash.clone()).collect::<Vec<_>>()).collect::<Vec<_>>();
    assert!(hashes(&dirs).contains(&a.hash), "A imported again is listed");
    library::migrate_identities(&dirs).unwrap();
    assert!(library::path_of(&dirs, &a).unwrap().is_dir(), "and the migration leaves it");
    // B edited back to A's contents and taken over: B's folder becomes A, which is there and exactly that
    copy_over(&src, &library::path_of(&dirs, &b).unwrap());
    let back = library::take_over_with(&dirs, &b, &mut no_game).unwrap();
    assert_eq!(back.to, a);
    assert!(hashes(&dirs).contains(&a.hash));
    assert!(!hashes(&dirs).contains(&b.hash), "B's folder went: its files are A's");
}

/// 6 (H4). A save of a profile that was deleted meanwhile never lands on a profile made again under its id, even at the
/// same generation (a save still on its way from the launcher, or another window).
#[test]
fn a_save_of_a_deleted_profile_never_lands_on_one_made_again() {
    let t = tmp("review-profile");
    let dirs = DataDirs::at(t.0.join("data"));
    let mut old = profile::create(&dirs, "My mods", None).unwrap();
    old.notes = "the deleted one's".into();
    profile::delete(&dirs, &old.id).unwrap();
    std::thread::sleep(std::time::Duration::from_millis(5));
    let made = profile::create(&dirs, "My mods", None).unwrap();
    assert_eq!((made.id.as_str(), made.generation), (old.id.as_str(), old.generation), "same id, same generation");
    let err = profile::save(&dirs, &old).unwrap_err();
    assert_eq!(err.code, ErrorCode::Conflict);
    assert!(err.message.contains("deleted and made again"), "{}", err.message);
    assert_eq!(profile::get(&dirs, &made.id).unwrap().notes, "", "the new profile is untouched");
    // its own saves go on
    profile::save(&dirs, &made).unwrap();
}

// ---- review of c13a9226 (Codex, rerun 1) ----

/// Two builds of a plugin in the Library; returns (older entry, newer entry).
fn two_plugin_builds(t: &Path, dirs: &DataDirs, first: &str, second: &str) -> (LibraryRef, LibraryRef) {
    put(t, &format!("p1/{first}"), &build_test_dll(&["kiln_plugin_main"], b"build 1"));
    put(t, &format!("p2/{second}"), &build_test_dll(&["kiln_plugin_main"], b"build 2"));
    let r = library::import(dirs, &[t.join(format!("p1/{first}")), t.join(format!("p2/{second}"))], &mut NoProgress).unwrap();
    assert_eq!(r.imported.len(), 2, "{r:?}");
    let one = library::path_of(dirs, &r.imported[0]).unwrap();
    let older = if std::fs::read(&one).unwrap() == build_test_dll(&["kiln_plugin_main"], b"build 1") { 0 } else { 1 };
    (r.imported[older].clone(), r.imported[1 - older].clone())
}

/// R1. The older plugin entry's DLL replaced with the newer build and a settings file put beside it: the take-over
/// reuses the newer entry (the same DLL) and that entry receives the settings file before the old folder goes.
#[test]
fn a_reused_plugin_destination_receives_the_players_other_files() {
    let t = tmp("r1-carry");
    let dirs = DataDirs::at(t.0.join("data"));
    let (old, newer) = two_plugin_builds(&t.0, &dirs, "hello.dll", "hello.dll");
    let folder = library::path_of(&dirs, &old).unwrap().parent().unwrap().to_path_buf();
    put(&folder, "hello.dll", &build_test_dll(&["kiln_plugin_main"], b"build 2"));
    put(&folder, "settings.ini", b"[hello]\nloud=1\n");
    put(&folder, ".notes", b"a dot file of the player's");
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert_eq!(done.to, newer);
    let dest = library::path_of(&dirs, &newer).unwrap().parent().unwrap().to_path_buf();
    assert_eq!(std::fs::read(dest.join("settings.ini")).unwrap(), b"[hello]\nloud=1\n", "the settings file is in the entry it became");
    assert_eq!(std::fs::read(dest.join(".notes")).unwrap(), b"a dot file of the player's");
    assert!(!folder.exists(), "the old folder went only after that");
    assert_eq!(kiln_manage::hash::sha256_file(&library::path_of(&dirs, &newer).unwrap()).unwrap().0, newer.hash);
}

/// R1. The same, but the reused entry has a settings file of its own with other contents: the take-over stops, names
/// the file and both folders, and deletes nothing.
#[test]
fn a_reused_destination_with_a_different_file_of_the_same_name_stops_the_take_over() {
    let t = tmp("r1-clash");
    let dirs = DataDirs::at(t.0.join("data"));
    let (old, newer) = two_plugin_builds(&t.0, &dirs, "hello.dll", "hello.dll");
    let folder = library::path_of(&dirs, &old).unwrap().parent().unwrap().to_path_buf();
    let dest = library::path_of(&dirs, &newer).unwrap().parent().unwrap().to_path_buf();
    put(&dest, "settings.ini", b"loud=0");
    put(&folder, "hello.dll", &build_test_dll(&["kiln_plugin_main"], b"build 2"));
    put(&folder, "settings.ini", b"loud=1");
    let err = library::take_over_with(&dirs, &old, &mut no_game).unwrap_err();
    assert_eq!(err.code, ErrorCode::Conflict, "{err:?}");
    assert!(err.message.contains("settings.ini") && err.message.contains(&folder.display().to_string()) && err.message.contains(&dest.display().to_string()), "{}", err.message);
    assert_eq!(std::fs::read(folder.join("settings.ini")).unwrap(), b"loud=1", "nothing deleted");
    assert_eq!(std::fs::read(dest.join("settings.ini")).unwrap(), b"loud=0", "nothing overwritten");
    assert!(library::record(&dirs, &old).is_ok());
}

/// R5. Two plugins whose file names give one id ("hello plugin.dll", "hello-plugin.dll"): the first one's DLL replaced
/// with the second build. The reused entry is checked by its own DLL's name, and taken as it is.
#[test]
fn a_plugin_destination_is_checked_by_its_own_dll_name() {
    let t = tmp("r5-name");
    let dirs = DataDirs::at(t.0.join("data"));
    let (old, newer) = two_plugin_builds(&t.0, &dirs, "hello plugin.dll", "hello-plugin.dll");
    assert_eq!(old.id, newer.id);
    let dll = library::path_of(&dirs, &old).unwrap();
    std::fs::write(&dll, build_test_dll(&["kiln_plugin_main"], b"build 2")).unwrap();
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap_or_else(|e| panic!("{e:?}"));
    assert_eq!(done.to, newer);
    let dest = library::path_of(&dirs, &newer).unwrap();
    assert!(!dest.parent().unwrap().join("hello plugin.dll").exists(), "the same bytes under the other name are not copied in");
    assert!(!dll.exists());
}

/// R3. A take-over stopped after its copy was put in place but before the copy's record was written: a retry finishes
/// that copy; a folder there with other files and no record stays a Conflict that says so.
#[test]
fn a_destination_left_without_its_record_is_finished_on_retry() {
    let t = tmp("r3-half");
    let dirs = DataDirs::at(t.0.join("data"));
    let old = library::import(&dirs, &[eml_src(&t.0, "src/a", "1.0.0", b"return 1")], &mut NoProgress).unwrap().imported[0].clone();
    let mut p = profile::create(&dirs, "p", None).unwrap();
    p.mods.eml.push(ProfileMod { id: old.id.clone(), version: old.version.clone(), hash: old.hash.clone(), enabled: true });
    let p = profile::save(&dirs, &p).unwrap();
    let folder = library::path_of(&dirs, &old).unwrap();
    put(&folder, "src/mod.lua", b"return 2");
    let h = kiln_manage::hash::dirhash(&folder).unwrap();
    let dest = dirs.library_entry(ModKind::Eml, &old.id, "1.0.0", &h).unwrap();
    kiln_manage::util::copy_tree(&folder, &dest).unwrap();
    put(&dest, "stray.txt", b"not the taken files");
    let err = library::take_over_with(&dirs, &old, &mut no_game).unwrap_err();
    assert_eq!(err.code, ErrorCode::Conflict);
    assert!(err.message.contains("no import record"), "{}", err.message);
    assert!(folder.is_dir() && dest.join("stray.txt").is_file(), "nothing deleted");
    std::fs::remove_file(dest.join("stray.txt")).unwrap();
    let done = library::take_over_with(&dirs, &old, &mut no_game).unwrap_or_else(|e| panic!("{e:?}"));
    assert_eq!(done.to.hash, h);
    assert_eq!(library::record(&dirs, &done.to).unwrap().hash, h, "the record was written");
    assert_eq!(profile::get(&dirs, &p.id).unwrap().mods.eml[0].hash, h);
    assert!(!folder.exists());
}

/// R4. The old folder cannot be deleted all the way (a file in it is locked: here a folder without write access): its
/// record is never dropped before the folder is out of the way, the result says what is left and where, and the next
/// migration removes the rest. A folder that cannot even be moved aside keeps its record, and a retry finishes.
#[test]
fn a_folder_that_cannot_be_removed_stays_findable_and_is_finished_later() {
    use std::os::unix::fs::PermissionsExt;
    let t = tmp("r4-locked");
    let dirs = DataDirs::at(t.0.join("data"));
    let old = library::import(&dirs, &[eml_src(&t.0, "src/a", "1.0.0", b"return 1")], &mut NoProgress).unwrap().imported[0].clone();
    let folder = library::path_of(&dirs, &old).unwrap();
    put(&folder, "src/mod.lua", b"return 2");
    let mode = |p: &Path, m: u32| std::fs::set_permissions(p, std::fs::Permissions::from_mode(m)).unwrap();
    mode(&folder.join("src"), 0o555);
    let done = library::take_over_with(&dirs, &old, &mut no_game);
    let left: Vec<PathBuf> = std::fs::read_dir(folder.parent().unwrap()).unwrap().map(|e| e.unwrap().path())
        .filter(|p| p.is_dir() && p != &library::path_of(&dirs, &done.as_ref().unwrap().to).unwrap()).collect();
    for l in &left { mode(&l.join("src"), 0o755); }
    let done = done.unwrap();
    let note = done.note.clone().unwrap_or_default();
    assert_eq!(left.len(), 1, "{left:?}");
    assert!(note.contains(&left[0].display().to_string()), "the result names what is left: {note:?}");
    library::migrate_identities(&dirs).unwrap();
    assert!(!left[0].exists(), "the next migration removes the rest");

    // the folder cannot be moved aside at all (its parent is read-only): it keeps its record and a retry finishes
    let t2 = tmp("r4-stuck");
    let dirs = DataDirs::at(t2.0.join("data"));
    let old = library::import(&dirs, &[eml_src(&t2.0, "src/a", "1.0.0", b"return 1")], &mut NoProgress).unwrap().imported[0].clone();
    let folder = library::path_of(&dirs, &old).unwrap();
    put(&folder, "src/mod.lua", b"return 2");
    let h = kiln_manage::hash::dirhash(&folder).unwrap();
    // its new entry is made first (as a retry would find it), then the parent of both is made read-only
    let dest = dirs.library_entry(ModKind::Eml, &old.id, "1.0.0", &h).unwrap();
    kiln_manage::util::copy_tree(&folder, &dest).unwrap();
    let mut rec: serde_json::Value = serde_json::from_slice(&std::fs::read(format!("{}.json", folder.display())).unwrap()).unwrap();
    rec["hash"] = h.clone().into();
    kiln_manage::util::write_json_atomic(&PathBuf::from(format!("{}.json", dest.display())), &rec).unwrap();
    let parent = folder.parent().unwrap().to_path_buf();
    mode(&parent, 0o555);
    let first = library::take_over_with(&dirs, &old, &mut no_game);
    mode(&parent, 0o755);
    let first = first.unwrap_or_else(|e| panic!("{e:?}"));
    assert!(first.note.as_deref().is_some_and(|n| n.contains("could not be removed")), "{first:?}");
    assert!(library::record(&dirs, &old).is_ok() && folder.is_dir(), "still a Library entry, findable");
    library::take_over_with(&dirs, &old, &mut no_game).unwrap();
    assert!(!folder.exists() && library::record(&dirs, &old).is_err(), "the retry finished");
}
