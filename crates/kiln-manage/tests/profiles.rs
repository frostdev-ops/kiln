//! The mod library (detection, content-addressed import) and profiles (create/save/resolve/activate, Z and copy
//! mode, chain.ini [profile], native.json state, play-once profiles) on temp folders.

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, DetectKind, LibraryRef};
use kiln_manage::manifest::{ModKind, RunSide};
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, chain, config, hash};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-profiles-{tag}-{}", std::process::id()));
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
    let d = root.join(format!("src/{id}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "{version}", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both"{extra} }}"#).as_bytes());
    put(&d, "M.dll", id.as_bytes());
    put(&d, ".kiln-source.json", b"{}"); // dot entries are not part of a mod
    d
}

fn native_mod(root: &Path, id: &str, phase: &str, after: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    let init = if phase == "dllmain" { "null" } else { "\"kiln_native_init\"" };
    put(&d, "native.json", format!(r#"{{ "format": 1, "id": "{id}", "version": "1.0.0", "dlls": ["{id}.dll"], "phase": "{phase}", "init": {init}, "after": [{after}] }}"#).as_bytes());
    put(&d, &format!("{id}.dll"), &build_test_dll(&["kiln_native_init"], id.as_bytes()));
    d
}

fn install_at(game: &Path, prefix: Option<PathBuf>) -> GameInstall {
    GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game.to_path_buf(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix, build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
    }
}

fn refs(r: &library::ImportReport, kind: ModKind, id: &str) -> LibraryRef {
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned().unwrap_or_else(|| panic!("{id} not imported: {r:?}"))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

#[test]
fn detection_by_manifest_and_exports() {
    let t = tmp("detect");
    let k = kiln_mod(&t.0, "new-items", "1.0.0", "");
    assert_eq!(library::detect(&k).unwrap().kind, DetectKind::Kiln);
    let n = native_mod(&t.0, "fps-unlock", "dllmain", "");
    assert_eq!(library::detect(&n).unwrap().kind, DetectKind::Native);
    let e = t.0.join("src/beast");
    put(&e, "mod.json", br#"{ "id": "beastmaster", "name": "Beastmaster", "version": "0.3.3", "emf": 21 }"#);
    put(&e, "beastmaster.dll", &build_test_dll(&["emf_mod_load"], b""));
    let d = library::detect(&e).unwrap();
    assert_eq!((d.kind, d.id.as_deref()), (DetectKind::Emf, Some("beastmaster")));
    // mod.json without a numeric "emf" is not an EMF mod
    put(&t.0, "src/notemf/mod.json", br#"{ "id": "x", "emf": "21" }"#);
    put(&t.0, "src/notemf/x.dll", b"");
    assert_eq!(library::detect(&t.0.join("src/notemf")).unwrap().kind, DetectKind::Unknown);
    put(&t.0, "src/pack/kiln-modpack.json", b"{}");
    assert_eq!(library::detect(&t.0.join("src/pack")).unwrap().kind, DetectKind::Modpack);
    let dll = |name: &str, exports: &[&str], extra: &[u8]| {
        let p = t.0.join("dlls").join(name);
        put(&t.0, &format!("dlls/{name}"), &build_test_dll(exports, extra));
        library::detect(&p).unwrap()
    };
    assert_eq!(dll("plug.dll", &["kiln_plugin_main"], b"").kind, DetectKind::Plugin);
    assert_eq!(dll("emfmod.dll", &["emf_mod_load"], b"").kind, DetectKind::Emf);
    let proxy = dll("reshade.dll", &["DirectInput8Create"], b"");
    assert_eq!(proxy.kind, DetectKind::BareDll);
    assert!(proxy.reason.contains("dllmain"), "{}", proxy.reason);
    assert_eq!(dll("kilnproxy.dll", &["DirectInput8Create", "KilnProxyInfo"], b"KilnProxy/2 0.2.0").kind, DetectKind::Unknown);
    let plain = dll("Fancy Thing.dll", &["Whatever"], b"");
    assert_eq!((plain.kind, plain.id.as_deref()), (DetectKind::BareDll, Some("fancy-thing")));
    put(&t.0, "dlls/readme.txt", b"hi");
    assert_eq!(library::detect(&t.0.join("dlls/readme.txt")).unwrap().kind, DetectKind::Unknown);
}

#[test]
fn import_is_content_addressed_and_immutable() {
    let t = tmp("import");
    let dirs = DataDirs::at(t.0.join("data"));
    let k = kiln_mod(&t.0, "new-items", "1.0.0", "");
    let r = library::import(&dirs, std::slice::from_ref(&k), &mut NoProgress).unwrap();
    let nr = refs(&r, ModKind::Kiln, "new-items");
    assert_eq!(nr.hash, hash::dirhash(&k).unwrap(), "the library hash is the source folder's kdh1 (dot entries skipped)");
    let folder = library::path_of(&dirs, &nr).unwrap();
    // a Kiln mod is known by its identity: the same mod with what a cook left in assets/cooked is the same entry
    put(&k, "assets/cooked/model.bin", b"made from this machine's game");
    put(&k, "assets/cooked/cook-report.json", b"{}");
    assert_ne!(hash::dirhash(&k).unwrap(), nr.hash);
    assert_eq!(hash::mod_identity(&k).unwrap(), nr.hash);
    let cooked = library::import(&dirs, std::slice::from_ref(&k), &mut NoProgress).unwrap();
    assert_eq!((cooked.imported.len(), cooked.existing.len()), (0, 1), "{cooked:?}");
    assert_eq!(refs(&cooked, ModKind::Kiln, "new-items"), nr);
    std::fs::remove_dir_all(k.join("assets")).unwrap();
    assert!(folder.ends_with(format!("library/kiln/new-items/1.0.0+{}", &nr.hash[5..13])));
    assert!(!folder.join(".kiln-source.json").exists());
    assert!(folder.with_extension("").with_file_name(format!("1.0.0+{}.json", &nr.hash[5..13])).exists());
    // the same again: existing; changed content: a second version folder
    let again = library::import(&dirs, std::slice::from_ref(&k), &mut NoProgress).unwrap();
    assert_eq!((again.imported.len(), again.existing.len()), (0, 1));
    std::fs::write(k.join("M.dll"), b"changed").unwrap();
    let r2 = library::import(&dirs, std::slice::from_ref(&k), &mut NoProgress).unwrap();
    assert_eq!(r2.imported.len(), 1);
    let l = library::list(&dirs, Some(ModKind::Kiln)).unwrap();
    assert_eq!(l[0].versions.len(), 2);
    assert!(l[0].versions[0].manifest.is_some());
    // bare DLLs become mod folders with a generated manifest
    put(&t.0, "dlls/fps.dll", &build_test_dll(&["DirectInput8Create"], b"fps"));
    put(&t.0, "dlls/plain.dll", &build_test_dll(&["DoThing"], b""));
    put(&t.0, "dlls/plug.dll", &build_test_dll(&["kiln_plugin_main"], b""));
    let r3 = library::import(&dirs, &[t.0.join("dlls/fps.dll"), t.0.join("dlls/plain.dll"), t.0.join("dlls/plug.dll")], &mut NoProgress).unwrap();
    assert_eq!(r3.imported.len(), 3, "{r3:?}");
    let fps = refs(&r3, ModKind::Native, "fps");
    let n = kiln_manage::manifest::read_native(&library::path_of(&dirs, &fps).unwrap()).unwrap();
    assert_eq!((n.phase, n.dlls.as_slice()), (kiln_manage::manifest::NativePhase::Dllmain, &["fps.dll".to_string()][..]));
    let plain = refs(&r3, ModKind::Native, "plain");
    assert_eq!(kiln_manage::manifest::read_native(&library::path_of(&dirs, &plain).unwrap()).unwrap().phase, kiln_manage::manifest::NativePhase::Early);
    let plug = refs(&r3, ModKind::Plugin, "plug");
    assert_eq!(plug.hash, hash::sha256_file(&t.0.join("dlls/plug.dll")).unwrap().0);
    assert!(library::path_of(&dirs, &plug).unwrap().ends_with("plug.dll"));
    // removal refuses while a profile lists the entry
    let mut p = profile::create(&dirs, "Uses it", None).unwrap();
    p.mods.kiln.push(pm(&nr, true));
    profile::save(&dirs, &p).unwrap();
    assert_eq!(library::remove(&dirs, &nr, false).unwrap_err().code, ErrorCode::Conflict);
    library::remove(&dirs, &nr, true).unwrap();
    assert!(!folder.exists());
}

#[cfg(feature = "zip")]
#[test]
fn zips_with_several_mods_import_each() {
    use std::io::Write;
    let t = tmp("zip");
    let dirs = DataDirs::at(t.0.join("data"));
    let z = t.0.join("pack.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&z).unwrap());
    let o = zip::write::SimpleFileOptions::default();
    for (name, data) in [
        ("Mods/a/kiln.mod.json", br#"{"id":"a","version":"1.0.0","apiVersion":"0.2","entryAssembly":"A.dll"}"#.to_vec()),
        ("Mods/a/A.dll", b"a".to_vec()),
        ("Mods/b/native.json", br#"{"id":"b","version":"2.0.0","dlls":["b.dll"]}"#.to_vec()),
        ("Mods/b/b.dll", build_test_dll(&["x"], b"")),
    ] {
        w.start_file(name, o).unwrap();
        w.write_all(&data).unwrap();
    }
    w.finish().unwrap();
    let d = library::detect(&z).unwrap();
    assert_eq!(d.items.len(), 2, "{d:#?}");
    let r = library::import(&dirs, &[z], &mut NoProgress).unwrap();
    assert_eq!(r.imported.len(), 2, "{r:?}");
    assert_eq!(library::list(&dirs, None).unwrap().len(), 2);
}

#[test]
fn profiles_save_with_generations_and_resolve() {
    let t = tmp("resolve");
    let dirs = DataDirs::at(t.0.join("data"));
    let lib = kiln_mod(&t.0, "lib", "1.2.0", "");
    let app = kiln_mod(&t.0, "app", "1.0.0", r#", "dependencies": { "lib": "^1.0" }"#);
    let off = kiln_mod(&t.0, "off", "1.0.0", "");
    let na = native_mod(&t.0, "na", "early", r#""nb""#);
    let nb = native_mod(&t.0, "nb", "boot", "");
    let r = library::import(&dirs, &[lib, app, off, na, nb], &mut NoProgress).unwrap();
    let p = profile::create(&dirs, "Server: Ember Isles", None).unwrap();
    assert_eq!(p.id, "server-ember-isles");
    assert_eq!(profile::create(&dirs, "Server: Ember Isles", None).unwrap().id, "server-ember-isles-2");
    let mut p2 = p.clone();
    p2.mods.kiln = vec![pm(&refs(&r, ModKind::Kiln, "app"), true), pm(&refs(&r, ModKind::Kiln, "lib"), true), pm(&refs(&r, ModKind::Kiln, "off"), false)];
    p2.mods.native = vec![pm(&refs(&r, ModKind::Native, "na"), true), pm(&refs(&r, ModKind::Native, "nb"), true)];
    let saved = profile::save(&dirs, &p2).unwrap();
    assert_eq!(saved.generation, 2);
    assert_eq!(profile::save(&dirs, &p2).unwrap_err().code, ErrorCode::Conflict, "a stale generation");
    let pre = profile::resolve_preview(&dirs, &saved, RunSide::Client).unwrap();
    assert_eq!(pre.kiln.order, ["lib", "app"]);
    assert!(pre.kiln.rejected.iter().any(|x| x.id == "off" && x.state == kiln_manage::resolver::ModState::Disabled));
    assert_eq!(pre.native.iter().map(|n| n.id.as_str()).collect::<Vec<_>>(), ["nb", "na"], "after = load after");
    assert!(pre.warnings.is_empty(), "{:?}", pre.warnings);
    assert_ne!(profile::fingerprint(&saved), profile::fingerprint(&p));
    assert_eq!(profile::list(&dirs).unwrap().len(), 2);
    profile::delete(&dirs, "server-ember-isles-2").unwrap();
    assert_eq!(profile::list(&dirs).unwrap().len(), 1);
}

/// A Kiln mod with `side` set ("server" on a client: Loot Leveler, Enemy Leveler).
fn kiln_mod_on(root: &Path, id: &str, side: &str, extra: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "{side}"{extra} }}"#).as_bytes());
    put(&d, "M.dll", id.as_bytes());
    d
}

/// Player report (Nexus, 2026-11-11): the launcher said Loot Leveler and Enemy Leveler (`side: "server"`) are "not
/// loaded on a client", so the player took them for server-only. On a client they start with the player's own world
/// (single-player or hosting; Kiln.Host ModManager.StartHosting), and the mods that need them come with them.
#[test]
fn server_mods_on_a_client_profile_start_with_the_players_world() {
    let t = tmp("hosted");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &[
        kiln_mod_on(&t.0, "loot-leveler", "server", ""),
        kiln_mod_on(&t.0, "hud", "client", ""),
        kiln_mod_on(&t.0, "loot-hud", "client", r#", "dependencies": { "loot-leveler": "^1" }"#),
        kiln_mod_on(&t.0, "broken", "server", r#", "dependencies": { "nope": "^1" }"#),
    ], &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "Single player", None).unwrap();
    p.mods.kiln = ["loot-leveler", "hud", "loot-hud", "broken"].iter().map(|id| pm(&refs(&r, ModKind::Kiln, id), true)).collect();
    let p = profile::save(&dirs, &p).unwrap();
    let pre = profile::resolve_preview(&dirs, &p, RunSide::Client).unwrap();
    let why = |id: &str| pre.kiln.rejected.iter().find(|x| x.id == id).map(|x| (x.state, x.reason.clone()));
    for x in &pre.kiln.rejected {
        assert!(!x.reason.contains("not loaded on a client"), "{}: {}", x.id, x.reason);
    }
    use kiln_manage::resolver::ModState::{Failed, Skipped};
    assert_eq!(pre.kiln.order, ["hud"], "the menu loads the client mod");
    assert_eq!(why("loot-leveler"), Some((Skipped, profile::WITH_WORLD.to_string())));
    assert_eq!(why("loot-hud"), Some((Skipped, profile::WITH_WORLD.to_string())), "needs loot-leveler: starts with it");
    assert_eq!(why("broken"), Some((Failed, "Will not start with your world: missing dependency nope ^1".to_string())));
    assert_eq!(pre.with_world, ["loot-leveler", "loot-hud"], "in the order they load with the world");

    // kiln.json "mods": {"hosted": false}: they stay off on this client, and say why
    let game = t.0.join("game");
    assert!(profile::hosted_server_mods(&game), "no kiln.json: on");
    put(&game, "kiln/kiln.json", br#"{ "policy": "any", "mods": { /* by hand */ "hosted": false, }, }"#);
    assert!(!profile::hosted_server_mods(&game));
    let off = profile::resolve_preview_for(&dirs, &p, RunSide::Client, profile::hosted_server_mods(&game)).unwrap();
    assert!(off.with_world.is_empty());
    let off_why = |id: &str| off.kiln.rejected.iter().find(|x| x.id == id).map(|x| (x.state, x.reason.clone()));
    assert_eq!(off_why("loot-leveler"), Some((Skipped, profile::HOSTED_OFF.to_string())));
    assert_eq!(off_why("broken"), Some((Skipped, profile::HOSTED_OFF.to_string())));
    for (text, on) in [(r#"{"mods":{"hosted":true}}"#, true), (r#"{"mods":{"hosted":"no"}}"#, true), (r#"{"mods":false}"#, true), ("{ not json", true)] {
        put(&game, "kiln/kiln.json", text.as_bytes());
        assert_eq!(profile::hosted_server_mods(&game), on, "{text}");
    }

    // a server loads them like any other mod; nothing starts "with the world" there
    let server = profile::resolve_preview(&dirs, &p, RunSide::Server).unwrap();
    assert_eq!(server.kiln.order, ["loot-leveler"]);
    assert!(server.with_world.is_empty());
    assert!(server.kiln.rejected.iter().any(|x| x.id == "hud" && x.state == Skipped && x.reason == "side 'client': not loaded on a server"));
}

#[cfg(unix)]
#[test]
fn activation_writes_profile_json_chain_and_native_state() {
    let t = tmp("activate");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    put(&game, "kiln/chain.ini", b"[chain]\r\nchain\\emf-dinput8.dll\r\n");
    let pfx = t.0.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    let r = library::import(&dirs, &[kiln_mod(&t.0, "new-items", "1.0.0", ""), native_mod(&t.0, "fps", "dllmain", ""),
        native_mod(&t.0, "creative", "early", "")], &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "Ember run", None).unwrap();
    p.mods.kiln = vec![pm(&refs(&r, ModKind::Kiln, "new-items"), true)];
    p.mods.native = vec![pm(&refs(&r, ModKind::Native, "fps"), true), pm(&refs(&r, ModKind::Native, "creative"), false)];
    let p = profile::save(&dirs, &p).unwrap();
    // Z mode
    let inst = install_at(&game, Some(pfx.clone()));
    let a = profile::activate(&dirs, &p.id, &inst).unwrap();
    assert_eq!((a.path_mode, a.generation, a.copied), (PathMode::Z, 1, 0));
    let f = profile::read_game_profile(&game).unwrap().unwrap();
    let lib_path = library::path_of(&dirs, &refs(&r, ModKind::Kiln, "new-items")).unwrap();
    assert_eq!(f.mods.kiln[0].path, kiln_manage::winpath::unix_to_z(&lib_path).unwrap());
    assert!(f.mods.kiln[0].path.starts_with("Z:\\"));
    assert_eq!(f.config_dir.as_deref().unwrap(), kiln_manage::winpath::unix_to_z(&dirs.profile(&p.id).join("config")).unwrap());
    assert_eq!(f.fingerprint.as_deref(), Some(profile::fingerprint(&p).as_str()));
    assert!(!f.mods.native[1].enabled);
    let c = chain::read(&game).unwrap();
    assert_eq!(c.chain[0].path, "chain\\emf-dinput8.dll", "[chain] kept");
    assert_eq!(c.profile.len(), 1);
    assert!(c.profile[0].path.starts_with("Z:\\") && c.profile[0].path.ends_with("\\fps.dll"), "{:?}", c.profile[0]);
    let st = config::read_native_state(&dirs.profile(&p.id).join("config")).unwrap();
    assert_eq!(st.mods["creative"].enabled, Some(false));
    assert_eq!(st.mods["fps"].enabled, Some(true));
    // the file matches the contract schema shape
    let v: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(game.join("kiln/profile.json")).unwrap()).unwrap();
    assert_eq!((v["format"].as_u64(), v["pathMode"].as_str()), (Some(1), Some("z")));
    assert_eq!(profile::active(&dirs, &inst).unwrap().unwrap().profile_id, p.id);
    // again: generation + 1
    assert_eq!(profile::activate(&dirs, &p.id, &inst).unwrap().generation, 2);
    // play once with this profile
    profile::write_once(&dirs, &p.id, &inst).unwrap();
    assert!(game.join("kiln/profile.once.json").exists());
    assert_eq!(chain::parse_once(&std::fs::read(game.join("kiln/chain/once.ini")).unwrap()).profile.len(), 1);
    // copy mode: no usable Z: drive
    std::fs::remove_file(pfx.join("dosdevices/z:")).unwrap();
    let a2 = profile::activate(&dirs, &p.id, &inst).unwrap();
    assert_eq!((a2.path_mode, a2.copied), (PathMode::Copy, 2), "the enabled kiln + native folders are copied");
    assert!(!game.join("kiln/profile.once.json").exists() && !game.join("kiln/chain/once.ini").exists(), "activation clears play-once files");
    let f2 = profile::read_game_profile(&game).unwrap().unwrap();
    assert_eq!(f2.mods.kiln[0].path, format!("profiles/{}/kiln/new-items", p.id));
    assert_eq!(f2.config_dir.as_deref(), Some(format!("profiles/{}/config", p.id).as_str()));
    let copy = game.join("kiln").join(&f2.mods.kiln[0].path);
    assert_eq!(hash::dirhash(&copy).unwrap(), refs(&r, ModKind::Kiln, "new-items").hash);
    assert_eq!(chain::read(&game).unwrap().profile[0].path, format!("profiles\\{}\\native\\fps\\fps.dll", p.id));
    // Kiln writes a config in the game copy; the next activation copies it back first
    put(&game, &format!("kiln/profiles/{}/config/new-items.json", p.id), b"{ \"x\": 1 }");
    assert_eq!(profile::activate(&dirs, &p.id, &inst).unwrap().copied, 0, "unchanged copies are kept");
    assert!(dirs.profile(&p.id).join("config/new-items.json").exists());
    // the player cooks the copy the game loads (kiln cook): it is still that mod, and its cooked files stay
    put(&copy, "assets/cooked/model.bin", b"made from this machine's game");
    put(&copy, "assets/cooked/cook-report.json", b"{}");
    assert_ne!(hash::dirhash(&copy).unwrap(), refs(&r, ModKind::Kiln, "new-items").hash);
    assert_eq!(profile::activate(&dirs, &p.id, &inst).unwrap().copied, 0, "a cooked copy is kept");
    assert_eq!(std::fs::read(copy.join("assets/cooked/model.bin")).unwrap(), b"made from this machine's game");
    // a changed shipped file is still replaced (with what the cook made for the old files)
    put(&copy, "M.dll", b"changed in the game folder");
    assert_eq!(profile::activate(&dirs, &p.id, &inst).unwrap().copied, 1);
    assert_eq!(hash::dirhash(&copy).unwrap(), refs(&r, ModKind::Kiln, "new-items").hash);
    // deactivate: the legacy layout, a backup of profile.json, [chain] kept
    profile::deactivate(&dirs, &inst).unwrap();
    assert!(profile::read_game_profile(&game).unwrap().is_none());
    let c = chain::read(&game).unwrap();
    assert_eq!((c.chain.len(), c.profile.len()), (1, 0));
    assert!(std::fs::read_dir(game.join("kiln/backups")).unwrap().count() >= 1);
}

/// Windows: native path mode. profile.json and chain.ini [profile] carry real Windows paths (no Z:, no `\\?\`), with
/// spaces and non-ASCII characters in the launcher data and game folders, and they resolve back to the library.
#[cfg(windows)]
#[test]
fn activation_native_mode_windows_paths() {
    let t = tmp("activate-win");
    activation_native_mode_windows_paths_at(t.0.join("verbatim"));
    // a plain path, as %LOCALAPPDATA% gives it (tmp() canonicalizes, which adds the verbatim prefix on Windows)
    activation_native_mode_windows_paths_at(kiln_manage::steam::strip_verbatim(t.0.join("plain")));
}

#[cfg(windows)]
fn activation_native_mode_windows_paths_at(base: PathBuf) {
    let dirs = DataDirs::at(base.join("Jörg Müller").join("AppData Local").join("Kiln"));
    let game = base.join("Program Files (x86)").join("Steam Ü").join("steamapps").join("common").join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    put(&game, "kiln/chain.ini", b"[chain]\r\nchain\\emf-dinput8.dll\r\n");
    let r = library::import(&dirs, &[kiln_mod(&base, "new-items", "1.0.0", ""), native_mod(&base, "fps", "dllmain", "")], &mut NoProgress)
        .unwrap();
    let mut p = profile::create(&dirs, "Ember run", None).unwrap();
    p.mods.kiln = vec![pm(&refs(&r, ModKind::Kiln, "new-items"), true)];
    p.mods.native = vec![pm(&refs(&r, ModKind::Native, "fps"), true)];
    let p = profile::save(&dirs, &p).unwrap();
    let mut inst = install_at(&game, None);
    inst.platform = Platform::Windows;
    inst.path_mode = PathMode::Native;
    assert_eq!(kiln_manage::game::path_mode(&inst, &dirs).unwrap(), PathMode::Native);
    let a = profile::activate(&dirs, &p.id, &inst).unwrap();
    assert_eq!((a.path_mode, a.copied), (PathMode::Native, 0));
    let f = profile::read_game_profile(&game).unwrap().unwrap();
    let lib_path = kiln_manage::steam::strip_verbatim(library::path_of(&dirs, &refs(&r, ModKind::Kiln, "new-items")).unwrap());
    let kp = &f.mods.kiln[0].path;
    assert_eq!(Path::new(kp), lib_path.as_path());
    assert!(kp.contains("Jörg Müller") && kp.contains("AppData Local") && kp.as_bytes()[1] == b':', "{kp}");
    assert!(!kp.starts_with("Z:") && !kp.starts_with(r"\\?\") && !kp.contains('/'), "{kp}");
    assert_eq!(Path::new(f.config_dir.as_deref().unwrap()), kiln_manage::steam::strip_verbatim(dirs.profile(&p.id).join("config")).as_path());
    let v: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(game.join("kiln/profile.json")).unwrap()).unwrap();
    assert_eq!(v["pathMode"].as_str(), Some("native"));
    // chain.ini [profile]: the dllmain DLL as an absolute Windows path that resolves to the library's file; [chain] kept
    let c = chain::read(&game).unwrap();
    assert_eq!(c.chain[0].path, "chain\\emf-dinput8.dll");
    assert_eq!(c.profile.len(), 1);
    let line = &c.profile[0].path;
    assert!(line.ends_with(r"\fps.dll") && !line.contains('/') && line.contains("Jörg Müller"), "{line}");
    let dll = kiln_manage::winpath::to_host(line, &inst).unwrap();
    assert!(dll.is_file(), "{}", dll.display());
    // written as UTF-8 (the proxy decodes UTF-8 first and falls back to the ANSI code page only for invalid UTF-8)
    let raw = std::fs::read(game.join("kiln/chain.ini")).unwrap();
    assert!(std::str::from_utf8(&raw).unwrap().contains("Jörg Müller"));
    let st = config::read_native_state(&dirs.profile(&p.id).join("config")).unwrap();
    assert_eq!(st.mods["fps"].enabled, Some(true));
    // play once with the profile: the same DLL line in chain/once.ini
    profile::write_once(&dirs, &p.id, &inst).unwrap();
    let once = chain::parse_once(&std::fs::read(game.join("kiln/chain/once.ini")).unwrap());
    assert_eq!(once.profile[0].path, *line);
    profile::deactivate(&dirs, &inst).unwrap();
    assert!(profile::read_game_profile(&game).unwrap().is_none());
}

#[test]
fn api_version_constant_matches_kiln_api() {
    let mods = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../managed/Kiln.Api/Mods.cs")).unwrap();
    assert!(mods.contains(&format!("ApiVersion = \"{}\"", profile::KILN_API)), "profile::KILN_API is stale");
    assert!(mods.contains(&format!("Waiting = \"{}\"", profile::GAME_HOSTED_WAITING)), "profile::GAME_HOSTED_WAITING is stale");
    assert!(mods.contains(&format!("Off = \"{}\"", profile::GAME_HOSTED_OFF)), "profile::GAME_HOSTED_OFF is stale");
}

#[test]
fn a_changed_active_profile_is_written_again_before_play() {
    use kiln_manage::launch::{self, LaunchMode, LaunchRequest};
    let t = tmp("refresh");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let r1 = library::import(&dirs, &[kiln_mod(&t.0, "frost", "0.1.0", "")], &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "Frost", None).unwrap();
    p.mods.kiln = vec![pm(&refs(&r1, ModKind::Kiln, "frost"), true)];
    let p = profile::save(&dirs, &p).unwrap();
    let inst = install_at(&game, None);
    profile::activate(&dirs, &p.id, &inst).unwrap();
    assert!(profile::refresh_active(&dirs, &inst).unwrap().is_none(), "current: nothing to write");
    let written = profile::read_game_profile(&game).unwrap().unwrap().generation;
    // the profile moves to 0.1.1 outside the game folder (kilnctl profile add, the profile pages)
    let r2 = library::import(&dirs, &[kiln_mod(&t.0, "frost", "0.1.1", "")], &mut NoProgress).unwrap();
    let mut p = profile::get(&dirs, &p.id).unwrap();
    p.mods.kiln = vec![pm(&refs(&r2, ModKind::Kiln, "frost"), true)];
    let p = profile::save(&dirs, &p).unwrap();
    // Play writes profile.json again before Steam starts the game
    let req = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
    launch::play_with(&dirs, &inst, &req, Some(&[][..]), &|_: &str| Ok(())).unwrap();
    let f = profile::read_game_profile(&game).unwrap().unwrap();
    assert_eq!(f.mods.kiln[0].version.as_deref(), Some("0.1.1"));
    assert_eq!(f.fingerprint.as_deref(), Some(profile::fingerprint(&p).as_str()));
    assert_eq!(f.generation, written + 1);
    assert!(profile::refresh_active(&dirs, &inst).unwrap().is_none(), "written once");
    // a profile.json of a profile this data folder does not have is left alone
    let mut other = f.clone();
    other.id = "not-here".into();
    std::fs::write(game.join("kiln/profile.json"), serde_json::to_string(&other).unwrap()).unwrap();
    assert!(profile::refresh_active(&dirs, &inst).unwrap().is_none());
}

/// A mod built against a newer Kiln than the game's runtime is named before Play (GameStatus.newer_mods): its `kilnBuild`
/// against the receipt's bundle version and that bundle's commit time.
#[test]
fn mods_built_against_a_newer_kiln_are_named() {
    use kiln_manage::manifest::KilnBuild;
    let t = tmp("newer");
    let dirs = DataDirs::at(t.0.join("data"));
    let stamp = |v: &str, c: &str| format!(r#", "kilnBuild": {{ "version": "{v}", "created": "{c}" }}"#);
    let newer = kiln_mod(&t.0, "item-blocks", "0.1.1", &stamp("0.2.0-dev+20261106.0f0a3f6", "2026-11-06T12:12:16Z"));
    let same = kiln_mod(&t.0, "frostbolt-block", "0.1.2", &stamp("0.2.0-dev+20261106.10a0d37", "2026-11-06T10:54:02Z"));
    let unstamped = kiln_mod(&t.0, "hello", "0.1.0", "");
    let off = kiln_mod(&t.0, "off", "1.0.0", &stamp("0.3.0", "2027-01-01T00:00:00Z"));
    let r = library::import(&dirs, &[newer, same, unstamped, off], &mut NoProgress).unwrap();
    let mut p = profile::create(&dirs, "Test S1 hosted", None).unwrap();
    p.mods.kiln = ["item-blocks", "frostbolt-block", "hello"].iter().map(|id| pm(&refs(&r, ModKind::Kiln, id), true)).collect();
    p.mods.kiln.push(pm(&refs(&r, ModKind::Kiln, "off"), false));
    let p = profile::save(&dirs, &p).unwrap();
    let runtime = KilnBuild::from_parts("0.2.0-dev+20261106.10a0d37", Some("2026-11-06T10:54:02Z")).unwrap();
    let named = profile::built_for_newer(&dirs, &p, &runtime);
    assert_eq!(named.len(), 1, "{named:?}");
    assert_eq!((named[0].id.as_str(), named[0].version.as_str(), named[0].built_with.as_str()), ("item-blocks", "0.1.1", "0.2.0-dev+20261106.0f0a3f6"));
    // a runtime of the same day whose bundle is gone has no time: the order is unknown, nothing is named
    let dayonly = KilnBuild::from_parts("0.2.0-dev+20261106.10a0d37", None).unwrap();
    assert!(profile::built_for_newer(&dirs, &p, &dayonly).is_empty());
    // an updated runtime names nothing
    let updated = KilnBuild::from_parts("0.2.0-dev+20261106.0f0a3f6", Some("2026-11-06T12:12:16Z")).unwrap();
    assert!(profile::built_for_newer(&dirs, &p, &updated).is_empty());
}
