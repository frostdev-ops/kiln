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

/// A runtime bundle in the launcher's data folder and the game's install receipt naming it. `reader`: the profile.json
/// reader revision its kiln-runtime.json declares (None: a runtime from before the declaration, as dev.23). Copy mode
/// names `../greeneye/<id>` only for a core that reads that form (profile::PROFILE_READER_GAME_MODS).
fn runtime_in_game(dirs: &DataDirs, g: &GameInstall, reader: Option<u32>, git: &str) {
    let s = g.path.parent().unwrap().join(format!("stage-{git}"));
    let mut m = serde_json::json!({ "format": 1, "kiln": "0.2.0-dev", "abi": 1, "api": "0.2.0",
        "components": { "core": { "sides": ["server", "client"], "default": true, "description": "core" } } });
    if let Some(r) = reader { m["profileReader"] = r.into(); }
    put(&s, "kiln-runtime.json", serde_json::to_string_pretty(&m).unwrap().as_bytes());
    put(&s, "core/kiln/kiln-core.dll", format!("core {git}").as_bytes());
    let opts = kiln_manage::bundle::CreateOptions { source_date_epoch: 1_794_000_000, git: git.into() };
    let zip = kiln_manage::bundle::create(&s, &g.path.parent().unwrap().join(format!("out-{git}")), &opts).unwrap();
    let b = kiln_manage::bundle::import(dirs, &zip).unwrap();
    // the core file the receipt lists, in the game, as an install leaves them
    let core = format!("core {git}");
    put(&g.path, "kiln/kiln-core.dll", core.as_bytes());
    put(&g.path, "kiln/.install.json", serde_json::to_string_pretty(&serde_json::json!({
        "format": 1, "installId": "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b", "installed": "2026-11-11T00:00:00Z",
        "runtime": { "version": b.version, "components": ["core"] }, "side": "client",
        "files": [{ "path": "kiln/kiln-core.dll", "sha256": kiln_manage::hash::sha256_hex(core.as_bytes()), "component": "core" }],
    })).unwrap().as_bytes());
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

/// A profile.json an older Kiln wrote names the Library entry, and no copy is in greeneye/ yet. The first launcher
/// start (the watcher's refresh) or Play places the copy; the file is written again to name it, so the next start
/// runs the copy that holds the mod's settings, not the Library entry they were taken from.
#[test]
fn a_profile_json_naming_the_library_entry_is_written_again_once_the_copy_is_placed() {
    use kiln_manage::launch::{LaunchMode, LaunchRequest};
    for through_play in [false, true] {
        let t = tmp(&format!("dev23-file-{through_play}"));
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game(&t.0, PathMode::Z);
        let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
        let pid = profile::create(&dirs, "Map", None).unwrap().id;
        activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
        // as dev.23 left it: the file names the Library entry, nothing is placed
        let copy = g.path.join("greeneye/unlock-map-markers");
        std::fs::remove_dir_all(&copy).unwrap();
        std::fs::remove_file(g.path.join("kiln/state/library-placements.json")).unwrap();
        let file = g.path.join("kiln/profile.json");
        let mut f: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        f["mods"]["greeneye"][0]["path"] = z(&library::path_of(&dirs, &v47).unwrap()).into();
        std::fs::write(&file, serde_json::to_vec(&f).unwrap()).unwrap();
        if through_play {
            let req = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true };
            kiln_manage::launch::play_with(&dirs, &g, &req, Some(&[]), &|_| Ok(())).unwrap();
        } else {
            profile::refresh_active(&dirs, &g).unwrap();
        }
        assert!(copy.join(MOD_FILES[0]).is_file());
        assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().mods.greeneye[0].path, z(&copy), "Play {through_play}");
    }
}

#[test]
fn copy_mode_names_the_same_copy() {
    let t = tmp("copy-mode");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Copy);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    // a runtime whose core does not read a path out of kiln/ (dev.23 and earlier call the whole profile invalid and load
    // no mods), or one the launcher cannot tell: the copy under kiln/profiles/, as before
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    assert_eq!(file.mods.greeneye[0].path, format!("profiles/{pid}/greeneye/unlock-map-markers"), "no receipt: the runtime is not known");
    runtime_in_game(&dirs, &g, None, "e3d6185");
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    assert_eq!(file.mods.greeneye[0].path, format!("profiles/{pid}/greeneye/unlock-map-markers"), "a runtime from before the form");
    // a core that reads it: relative to <game>/kiln/, as copy mode's other paths, and not a copy under kiln/profiles/
    // that the next activation replaces (with the settings in it)
    runtime_in_game(&dirs, &g, Some(2), "a633659");
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    assert_eq!(file.mods.greeneye[0].path, "../greeneye/unlock-map-markers");
    assert!(g.path.join("greeneye/unlock-map-markers/mod.json").is_file());
    // the profile names the copy in greeneye/: no unused copy under kiln/profiles/ (the earlier one goes)
    let a = profile::activate(&dirs, &pid, &g).unwrap();
    assert!(!g.path.join(format!("kiln/profiles/{pid}/greeneye/unlock-map-markers")).exists());
    assert_eq!(a.copied, 0, "{a:?}");
    // back to a runtime from before the form: the copy under kiln/profiles/ again, which the profile names
    runtime_in_game(&dirs, &g, None, "e3d6185");
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    assert_eq!(file.mods.greeneye[0].path, format!("profiles/{pid}/greeneye/unlock-map-markers"));
    assert!(g.path.join(format!("kiln/profiles/{pid}/greeneye/unlock-map-markers/mod.json")).is_file());
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

// ---- review of 5af1974: game-side path forms, and one mod's trouble never stops an activation ----

/// A Greeneye mod folder whose mod.json gives `id` (a stand-in DLL), imported on its own.
fn other_mod(dirs: &DataDirs, t: &Path, id: &str) -> LibraryRef {
    let src = t.join("src").join(format!("{id}-src"));
    put(&src, "mod.json", format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","dll":"m.dll","side":"client","api":2}}"#).as_bytes());
    put(&src, "m.dll", &build_test_dll(&["GreeneyeModStart"], id.as_bytes()));
    import_into(dirs, &src)
}

/// What the game's profile reader accepts for a Greeneye entry in copy mode: a path relative to `<game>/kiln/` without
/// `..`, or exactly `../greeneye/<folder>` where the folder is the entry's id and a safe Windows folder name
/// (engine_mods::is_game_folder_name). Anything else makes the whole profile invalid there (no mod loads).
fn copy_mode_path_ok(id: &str, path: &str) -> bool {
    match path.strip_prefix("../greeneye/") {
        Some(name) => name == id && engine_mods::is_game_folder_name(name),
        None => !path.split(['/', '\\']).any(|c| c == "..") && !path.starts_with(['/', '\\']) && !path.contains(':'),
    }
}

#[test]
fn copy_mode_paths_pass_the_games_folder_name_rule_and_an_unsafe_id_is_not_placed() {
    let t = tmp("copy-rule");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Copy);
    runtime_in_game(&dirs, &g, Some(2), "a633659");
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    // `nul` is a Kiln id but no folder Windows can hold
    let bad = other_mod(&dirs, &t.0, "nul");
    assert_eq!(bad.id, "nul");
    assert!(!engine_mods::is_game_folder_name(&bad.id));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true), pm(&bad, true)]);
    for m in &file.mods.greeneye {
        assert!(copy_mode_path_ok(&m.id, &m.path), "{} -> {}", m.id, m.path);
    }
    assert_eq!(file.mods.greeneye[0].path, "../greeneye/unlock-map-markers");
    assert_eq!(file.mods.greeneye[1].path, format!("profiles/{pid}/greeneye/nul"), "the copy path as before dev.24");
    assert!(!g.path.join("greeneye/nul").exists(), "not placed");
    // the other path modes: the Library entry, as before
    let tz = tmp("z-rule");
    let gz = game(&tz.0, PathMode::Z);
    let file = activate_with(&dirs, &gz, &pid, vec![pm(&bad, true)]);
    assert_eq!(file.mods.greeneye[0].path, z(&library::path_of(&dirs, &bad).unwrap()));
    assert!(!gz.path.join("greeneye/nul").exists());
}

#[test]
fn a_greeneye_mod_that_cannot_be_placed_does_not_stop_the_activation() {
    let t = tmp("soft");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let good = import_into(&dirs, &release(&t.0, "1.3.47"));
    let broken = other_mod(&dirs, &t.0, "find");
    // an older Kiln let the mod write into its entry, and the import record is gone: nothing proves what is the mod's
    let lib = library::path_of(&dirs, &broken).unwrap();
    put(&lib, "find.ini", b"[x]\n");
    std::fs::remove_file(lib.with_file_name(format!("{}.json", lib.file_name().unwrap().to_string_lossy()))).unwrap();
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.greeneye = vec![pm(&broken, true), pm(&good, true)];
    profile::save(&dirs, &p).unwrap();
    let rep = engine_mods::place(&dirs, &p, &g.path).expect("one mod's trouble is not the activation's");
    assert!(rep.warnings.iter().any(|c| c.starts_with("greeneye:find: not placed in ")), "listed with its reason: {rep:?}");
    assert_eq!(rep.placed, ["greeneye:unlock-map-markers"], "{rep:?}");
    let a = profile::activate(&dirs, &pid, &g).expect("the profile switch and Play go on");
    assert!(a.warnings.iter().any(|c| c.starts_with("greeneye:find: not placed in ")), "the player is told: {a:?}");
    let file = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(file.mods.greeneye[0].path, z(&lib), "the broken one runs from the Library, as before");
    assert_eq!(file.mods.greeneye[1].path, z(&g.path.join("greeneye/unlock-map-markers")));
}

/// A copy whose DLL the player replaced is not Kiln's to update or remove. It stays, the switch goes on, and every
/// activation says where it is and that Greeneye's own loader may still run it.
#[test]
fn a_copy_the_player_edited_stays_and_is_reported_with_its_folder() {
    let t = tmp("edited");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    let v48 = import_into(&dirs, &release(&t.0, "1.3.48"));
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
    let copy = g.path.join("greeneye/unlock-map-markers");
    put(&copy, "unlock-map-markers.dll", b"the player's own build");

    // an update: the edited copy is neither replaced nor deleted, and the new version runs from the Library
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.greeneye = vec![pm(&v48, true)];
    profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &pid, &g).expect("one edited copy does not stop the switch");
    assert_eq!(std::fs::read(copy.join("unlock-map-markers.dll")).unwrap(), b"the player's own build");
    let kept = a.retained.iter().find(|r| r.path == copy).unwrap_or_else(|| panic!("the copy is reported: {a:?}"));
    assert!(kept.may_run && !kept.reason.is_empty(), "{kept:?}");
    let file = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(file.mods.greeneye[0].path, z(&library::path_of(&dirs, &v48).unwrap()), "the profile does not name the edited copy");

    // switched off: still there, still reported
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.greeneye = vec![pm(&v48, false)];
    profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &pid, &g).expect("switching it off goes on too");
    assert!(copy.join("unlock-map-markers.dll").is_file());
    assert!(a.retained.iter().any(|r| r.path == copy && r.may_run), "{a:?}");
}

#[test]
fn a_linked_greeneye_folder_does_not_stop_the_activation() {
    let t = tmp("linked");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
    std::fs::create_dir_all(t.0.join("elsewhere")).unwrap();
    std::os::unix::fs::symlink(t.0.join("elsewhere"), g.path.join("greeneye")).unwrap();
    let pid = profile::create(&dirs, "Map", None).unwrap().id;
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.greeneye = vec![pm(&v47, true)];
    profile::save(&dirs, &p).unwrap();
    let rep = engine_mods::place(&dirs, &p, &g.path).expect("placement goes on");
    assert!(rep.warnings.iter().any(|c| c.starts_with("greeneye:unlock-map-markers: not placed in ")), "{rep:?}");
    let a = profile::activate(&dirs, &pid, &g).expect("activation goes on");
    assert!(a.warnings.iter().any(|c| c.starts_with("greeneye:unlock-map-markers: not placed in ")), "the player is told: {a:?}");
    let file = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(file.mods.greeneye[0].path, z(&library::path_of(&dirs, &v47).unwrap()));
    assert!(std::fs::read_dir(t.0.join("elsewhere")).unwrap().next().is_none(), "nothing written through the link");
}

/// Review R6 (the cleanup matrix's Greeneye row): "No profile" (deactivate) and uninstall (unplace) must stop a
/// profile's Greeneye mods. A copy the mod only wrote its own files into is removed, those files wait in
/// kiln/kept-mod-files/greeneye/<id>/ and come back with the next activation; kiln-greeneye's drop-in scan of the legacy
/// layout and Greeneye Loader would otherwise run the copy without the profile. A copy whose shipped files changed is
/// the player's edit and stays, reported.
#[test]
fn no_profile_stops_greeneye_mods_and_keeps_what_they_wrote() {
    for (uninstall, shipped) in [(false, false), (true, false), (false, true), (true, true)] {
        let t = tmp(&format!("cleanup-{uninstall}-{shipped}"));
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game(&t.0, PathMode::Z);
        let v47 = import_into(&dirs, &release(&t.0, "1.3.47"));
        let pid = profile::create(&dirs, "Map", None).unwrap().id;
        activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
        let copy = g.path.join("greeneye/unlock-map-markers");
        put(&copy, "minimap_config.ini", b"[MinimapV2]\nZoom=3\n");
        put(&copy, "unlock_map_markers.log", b"loaded\n");
        if shipped { put(&copy, "veins.bin", b"edited by the player"); }
        let report = if uninstall { engine_mods::unplace(&dirs, &g.path).unwrap() } else { profile::deactivate_report(&dirs, &g).unwrap() };
        let kept = g.path.join("kiln/kept-mod-files/greeneye/unlock-map-markers");
        if shipped {
            assert!(report.retained.iter().any(|r| r.path == copy && r.may_run), "{uninstall}/{shipped}: {report:?}");
            assert_eq!(std::fs::read(copy.join("veins.bin")).unwrap(), b"edited by the player");
            assert!(copy.join("minimap_config.ini").is_file(), "the whole folder stays");
            continue;
        }
        assert!(!copy.exists() && report.removed.contains(&copy), "{uninstall}: the copy no longer runs: {report:?}");
        assert!(report.retained.is_empty(), "{report:?}");
        assert_eq!(std::fs::read_to_string(kept.join("minimap_config.ini")).unwrap(), "[MinimapV2]\nZoom=3\n");
        assert!(kept.join("unlock_map_markers.log").is_file());
        if !uninstall {
            let file = activate_with(&dirs, &g, &pid, vec![pm(&v47, true)]);
            assert_eq!(file.mods.greeneye[0].path, z(&copy));
            assert_eq!(std::fs::read_to_string(copy.join("minimap_config.ini")).unwrap(), "[MinimapV2]\nZoom=3\n", "back with the profile");
        }
    }
}
