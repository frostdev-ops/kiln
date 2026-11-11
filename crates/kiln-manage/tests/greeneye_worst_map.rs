//! The Worst Map Mod (Nexus mod 220, Greeneye Loader API 2) as its release zip ships it: `dbgcore.dll` next to the game
//! and `greeneye/unlock-map-markers/` with its mod.json, DLL, data files and `hd/` textures (inert stand-ins here).
//! What the Library imports from the zip, a wrapped zip, a zip with backslash names, a 7z and the folder alone, and
//! what the profile hands kiln-greeneye.
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::library::{self, ImportReport};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::task::NoProgress;
use std::io::Write;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    static N: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
    let n = N.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    let p = std::env::temp_dir().join(format!("km-greeneye-wmm-{tag}-{}-{n}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

const MOD_JSON: &[u8] = br#"{
    "id": "unlock-map-markers",
    "name": "The Worst Map Mod",
    "version": "1.3.47",
    "dll": "unlock-map-markers.dll",
    "side": "client",
    "api": 2,
    "description": "In game map markers and a minimap.",
    "author": "GreeneyeDS"
}
"#;

/// The mod's own files, relative to its folder (build.bat's bin\loader\ and the files it writes at run time are not
/// part of the zip).
const MOD_FILES: [&str; 10] = ["unlock-map-markers.dll", "mod.json", "veins.bin", "ground.bin", "halls.bin", "traps.bin",
    "roads.bin", "water.bin", "hd/mat_1.png", "hd/materials.txt"];

fn files(prefix: &str) -> Vec<(String, Vec<u8>)> {
    let mut v = vec![
        (format!("{prefix}dbgcore.dll"), build_test_dll(&["MiniDumpWriteDump"], b"Greeneye Loader 1.2.2")),
        (format!("{prefix}README.txt"), b"IN GAME MAP MARKERS AND MINI MAP - THE WORST MAP MOD".to_vec()),
    ];
    for f in MOD_FILES {
        let data = match f {
            "unlock-map-markers.dll" => build_test_dll(&["GreeneyeModHot", "GreeneyeModStart"], b""),
            "mod.json" => MOD_JSON.to_vec(),
            other => format!("data {other}").into_bytes(),
        };
        v.push((format!("{prefix}greeneye/unlock-map-markers/{f}"), data));
    }
    v
}

fn zip_of(entries: &[(String, Vec<u8>)], out: &Path, backslash: bool) {
    let mut w = zip::ZipWriter::new(std::fs::File::create(out).unwrap());
    for (name, data) in entries {
        let name = if backslash { name.replace('/', "\\") } else { name.clone() };
        w.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
        w.write_all(data).unwrap();
    }
    w.finish().unwrap();
}

fn import(paths: &[PathBuf]) -> (Tmp, DataDirs, ImportReport) {
    let t = tmp("data");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, paths, &mut NoProgress).unwrap();
    (t, dirs, r)
}

fn summary(r: &ImportReport) -> String {
    let mut out = Vec::new();
    for x in r.imported.iter().chain(&r.existing) {
        out.push(format!("+ {}/{}", x.kind.as_str(), x.id));
    }
    for s in &r.skipped {
        out.push(format!("- {}: {}", s.path, s.reason));
    }
    for n in &r.notes {
        out.push(format!("i {}", n.text));
    }
    out.join("\n")
}

/// Every way a player hands the release to the Library: the zip, the zip with a wrapper folder, a zip written with
/// backslash names, a 7z, the unpacked folder and the mod's own folder.
fn shapes(t: &Path) -> Vec<(&'static str, PathBuf)> {
    let mut out = Vec::new();
    let z = t.join("In Game Map Markers and Mini Map - The Worst Map Mod-220-1-3-47-1794300000.zip");
    zip_of(&files(""), &z, false);
    out.push(("zip", z));
    let w = t.join("wrapped.zip");
    zip_of(&files("The Worst Map Mod 1.3.47/"), &w, false);
    out.push(("wrapped zip", w));
    let b = t.join("backslash.zip");
    zip_of(&files(""), &b, true);
    out.push(("backslash zip", b));
    let d = t.join("unpacked");
    for (name, data) in files("") {
        put(&d, &name, &data);
    }
    out.push(("folder", d.clone()));
    out.push(("mod folder", d.join("greeneye/unlock-map-markers")));
    #[cfg(feature = "sevenz")]
    {
        use sevenz_rust2::{ArchiveEntry, ArchiveWriter};
        let s = t.join("worst-map.7z");
        let mut a = ArchiveWriter::create(&s).unwrap();
        for (name, data) in files("") {
            a.push_archive_entry(ArchiveEntry::new_file(&name), Some(&data[..])).unwrap();
        }
        a.finish().unwrap();
        out.push(("7z", s));
    }
    out
}

#[test]
fn every_shape_of_the_release_is_one_greeneye_mod_with_all_its_files() {
    let t = tmp("shapes");
    for (how, path) in shapes(&t.0) {
        let (_k, dirs, r) = import(&[path]);
        eprintln!("{how}:\n{}", summary(&r));
        let got: Vec<_> = r.imported.iter().map(|x| (x.kind, x.id.as_str())).collect();
        assert_eq!(got, [(ModKind::Greeneye, "unlock-map-markers")], "{how}: {}", summary(&r));
        assert!(r.skipped.is_empty(), "{how}: {}", summary(&r));
        let folder = library::path_of(&dirs, &r.imported[0]).unwrap();
        for f in MOD_FILES {
            assert!(folder.join(f).is_file(), "{how}: {f} kept in {}", folder.display());
        }
        assert!(!folder.join("dbgcore.dll").exists(), "{how}: Greeneye's loader is not part of the mod");
        let rec = library::record(&dirs, &r.imported[0]).unwrap();
        assert_eq!((rec.version.as_str(), rec.name.as_deref(), rec.manifest_id.as_deref()),
            ("1.3.47", Some("The Worst Map Mod"), None), "{how}");
    }
}

// ---- the profile: where kiln-greeneye (or Greeneye's own loader) runs the mod from ----

use kiln_manage::engine_mods;
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::hash;
use kiln_manage::library::LibraryRef;
use kiln_manage::profile::{self, ProfileMod};

fn game(t: &Path, mode: PathMode) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    // copy mode: a prefix without the Z: drive (the launcher picks the mode from the prefix)
    if mode == PathMode::Z {
        std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    }
    GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: mode, source: "manual".into(),
    }
}

fn z(p: &Path) -> String {
    format!("Z:{}", p.display()).replace('/', "\\")
}

/// The release zip of `version` (its DLL's bytes differ per version, as a real update's do).
fn release(t: &Path, version: &str) -> PathBuf {
    let mut entries = files("");
    for (name, data) in &mut entries {
        if name.ends_with("mod.json") {
            *data = String::from_utf8_lossy(MOD_JSON).replace("1.3.47", version).into_bytes();
        } else if name.ends_with("unlock-map-markers.dll") {
            *data = build_test_dll(&["GreeneyeModHot", "GreeneyeModStart"], version.as_bytes());
        }
    }
    let zip = t.join(format!("worst-map-{version}.zip"));
    zip_of(&entries, &zip, false);
    zip
}

fn import_into(dirs: &DataDirs, zip: &Path) -> LibraryRef {
    let r = library::import(dirs, &[zip.to_path_buf()], &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.kind == ModKind::Greeneye).cloned().unwrap_or_else(|| panic!("{}", summary(&r)))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

fn activate_with(dirs: &DataDirs, g: &GameInstall, pid: &str, mods: Vec<ProfileMod>) -> profile::ProfileFile {
    let mut p = profile::get(dirs, pid).unwrap();
    p.mods.greeneye = mods;
    profile::save(dirs, &p).unwrap();
    profile::activate(dirs, pid, g).unwrap();
    profile::read_game_profile(&g.path).unwrap().unwrap()
}

#[test]
fn the_mod_runs_from_greeneye_in_the_game_folder_and_keeps_what_it_wrote_across_updates() {
    let t = tmp("placed");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    // Greeneye's API gives a mod its folder as ...\greeneye\<id>\ and the mod keeps its settings and logs there
    // (minimap_config.ini, unlock_map_markers.log): Kiln's copy is where Greeneye Loader would have it
    let copy = g.path.join("greeneye/unlock-map-markers");
    assert_eq!(file.mods.greeneye[0].path, z(&copy), "the profile names the game-folder copy");
    for f in MOD_FILES {
        assert!(copy.join(f).is_file(), "{f} in {}", copy.display());
    }
    assert_eq!(engine_mods::marker(&copy).map(|m| m.version), Some("1.3.47".into()));
    // the game runs: the mod writes its settings and its log into its folder
    put(&copy, "minimap_config.ini", b"[MinimapV2]\nZoom=3\n");
    put(&copy, "unlock_map_markers.log", b"[2026-11-11 10:00:00.000] The Worst Map Mod 1.3.47 loaded\n");
    let lib47 = library::path_of(&dirs, &v47).unwrap();
    assert_eq!(hash::dirhash(&lib47).unwrap(), v47.hash, "the Library entry stays as imported");
    assert!(profile::refresh_active(&dirs, &g).unwrap().is_none(), "nothing to redo before the next start");

    // the next day's update: the new version replaces the copy, what the mod wrote stays
    let v48 = import_into(&dirs, &release(&t.0, "1.3.48"));
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v48, true)]);
    assert_eq!(file.mods.greeneye[0].path, z(&copy));
    assert!(std::fs::read_to_string(copy.join("mod.json")).unwrap().contains("1.3.48"));
    assert_eq!(std::fs::read(copy.join("unlock-map-markers.dll")).unwrap(),
        std::fs::read(library::path_of(&dirs, &v48).unwrap().join("unlock-map-markers.dll")).unwrap());
    assert_eq!(std::fs::read_to_string(copy.join("minimap_config.ini")).unwrap(), "[MinimapV2]\nZoom=3\n", "the settings survive the update");
    assert!(copy.join("unlock_map_markers.log").is_file());

    // switched off: the copy goes (Greeneye Loader would run anything in greeneye\), the settings wait for it
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v48, false)]);
    assert!(!copy.exists(), "a switched-off mod is not left where a loader runs it");
    assert!(!file.mods.greeneye[0].enabled);
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v48, true)]);
    assert_eq!(file.mods.greeneye[0].path, z(&copy));
    assert_eq!(std::fs::read_to_string(copy.join("minimap_config.ini")).unwrap(), "[MinimapV2]\nZoom=3\n", "and come back with it");
}

#[test]
fn copy_mode_names_the_same_copy() {
    let t = tmp("copy-mode");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Copy);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    // relative to <game>/kiln/, as copy mode's other paths: not a copy under kiln/profiles/ that the next activation
    // replaces (with the settings in it)
    assert_eq!(file.mods.greeneye[0].path, "../greeneye/unlock-map-markers");
    assert!(g.path.join("greeneye/unlock-map-markers/mod.json").is_file());
}

#[test]
fn a_folder_the_player_put_in_greeneye_stays_theirs() {
    let t = tmp("own");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    // the player's second try: the release copied into the game folder by hand
    for (name, data) in files("") {
        put(&g.path, &name, &data);
    }
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let before = hash::dirhash(&g.path.join("greeneye/unlock-map-markers")).unwrap();
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    assert_eq!(hash::dirhash(&g.path.join("greeneye/unlock-map-markers")).unwrap(), before, "never touched");
    assert!(engine_mods::marker(&g.path.join("greeneye/unlock-map-markers")).is_none());
    assert_eq!(file.mods.greeneye[0].path, z(&library::path_of(&dirs, &v47).unwrap()), "the Library copy, as before");
}

#[test]
fn an_entry_the_mod_wrote_into_under_dev23_is_taken_apart_on_the_first_placement() {
    // Kiln 0.2.0-dev.23 ran Greeneye mods from the Library: the mod wrote its settings and logs into the Library entry.
    let t = tmp("dev23");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let lib = library::path_of(&dirs, &v47).unwrap();
    std::thread::sleep(std::time::Duration::from_millis(20));
    put(&lib, "minimap_config.ini", b"[MinimapV2]\nZoom=5\n");
    put(&lib, "looted_chests.ini", b"[world]\nchest1=1\n");
    put(&lib, "unlock_map_markers.log", b"loaded\n");
    assert_ne!(hash::dirhash(&lib).unwrap(), v47.hash);
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    let copy = g.path.join("greeneye/unlock-map-markers");
    assert_eq!(file.mods.greeneye[0].path, z(&copy));
    assert_eq!(hash::dirhash(&lib).unwrap(), v47.hash, "the Library entry is as imported again");
    assert_eq!(std::fs::read_to_string(copy.join("minimap_config.ini")).unwrap(), "[MinimapV2]\nZoom=5\n", "the settings moved with the mod");
    assert!(copy.join("looted_chests.ini").is_file() && copy.join("unlock_map_markers.log").is_file());
}

#[test]
fn an_entry_the_mod_wrote_into_does_not_stop_activation_when_greeneyes_loader_is_installed() {
    // dev.23 ran the mod from the Library, then the player copied Greeneye's dbgcore.dll next to the game (the second
    // try): activation placed the mod for Greeneye Loader and refused with "library hash mismatch"
    let t = tmp("dev23-loader");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let lib = library::path_of(&dirs, &v47).unwrap();
    std::thread::sleep(std::time::Duration::from_millis(20));
    put(&lib, "minimap_config.ini", b"[MinimapV2]\nZoom=5\n");
    put(&g.path, "dbgcore.dll", &build_test_dll(&["MiniDumpWriteDump"], b"Greeneye Loader 1.2.2"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.greeneye = vec![pm(&v47, true)];
    profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &pid, &g);
    assert!(a.is_ok(), "{:?}", a.err());
    assert!(g.path.join("greeneye/unlock-map-markers/minimap_config.ini").is_file());
}

/// The real release, when a maintainer has it (it is not in the repository): `KILN_WMM_ZIP=<the Nexus zip> cargo test -p
/// kiln-manage --features full --test greeneye_worst_map -- --ignored --nocapture`. The same checks as the stand-in.
#[test]
#[ignore]
fn the_real_release_zip_when_given() {
    let Some(zip) = std::env::var_os("KILN_WMM_ZIP").map(PathBuf::from) else { return };
    let t = tmp("real");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = library::import(&dirs, &[zip], &mut NoProgress).unwrap();
    eprintln!("import:\n{}", summary(&r));
    let v = r.imported.iter().find(|x| x.kind == ModKind::Greeneye).cloned().expect("a Greeneye mod");
    assert_eq!((r.imported.len(), v.id.as_str()), (1, "unlock-map-markers"));
    let folder = library::path_of(&dirs, &v).unwrap();
    let mut kept: Vec<String> = Vec::new();
    for e in std::fs::read_dir(&folder).unwrap().flatten() {
        kept.push(e.file_name().to_string_lossy().into_owned());
    }
    kept.sort();
    eprintln!("library entry {}: {kept:?}", folder.display());
    assert!(!folder.join("dbgcore.dll").exists());
    let g = game(&t.0, PathMode::Z);
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v, true)]);
    eprintln!("profile.json greeneye: {}", serde_json::to_string(&file.mods.greeneye).unwrap());
    let copy = g.path.join("greeneye/unlock-map-markers");
    assert_eq!(file.mods.greeneye[0].path, z(&copy));
    assert_eq!(hash::dirhash(&copy.join("hd")).unwrap(), hash::dirhash(&folder.join("hd")).unwrap());
    assert!(copy.join("unlock-map-markers.dll").is_file() && copy.join("veins.bin").is_file());
}

#[test]
fn play_once_keeps_the_active_selection() {
    // the copies in greeneye/ are the active profile's (Greeneye Loader and drop-ins read that folder): a play-once start
    // with other Greeneye mods would run the Library entries (and let them write there) next to them
    let t = tmp("once");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    let same = profile::create(&dirs, "Same", None).unwrap();
    let mut p = profile::get(&dirs, &same.id).unwrap();
    p.mods.greeneye = vec![pm(&v47, true)];
    profile::save(&dirs, &p).unwrap();
    let once = profile::write_once(&dirs, &same.id, &g).unwrap();
    assert_eq!(once.mods.greeneye[0].path, z(&g.path.join("greeneye/unlock-map-markers")));
    let none = profile::create(&dirs, "None", None).unwrap();
    let e = profile::write_once(&dirs, &none.id, &g).unwrap_err();
    assert_eq!(e.code, kiln_manage::ErrorCode::Unsupported, "{e:?}");
    assert!(e.message.contains("Greeneye"), "{}", e.message);
}
