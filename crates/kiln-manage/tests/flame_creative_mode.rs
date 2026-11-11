//! Creative Mode (Nexus mod 146, a Flame mod) as its release ships it: `mods/creative/` with mod.json, flame.json, its DLL,
//! items.json, `sigs/<exe timestamp>-<image size>.json` per game build and `symbols/` recipes (inert stand-ins here).
//! The mod reads those files from `<game>\mods\<its id>\`, not from the folder its DLL was loaded from (its own log under
//! Kiln: "Mod directory: S:\...\Enshrouded\mods\creative" for a DLL kiln-flame loaded from the Library), so it runs from
//! Kiln's copy there, as Greeneye mods run from `<game>/greeneye/<id>/` (greeneye_worst_map.rs).
#![cfg(unix)]

use kiln_manage::engine_mods;
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::hash;
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
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
    static N: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
    let n = N.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    let p = std::env::temp_dir().join(format!("km-flame-cm-{tag}-{}-{n}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// The build key of Enshrouded 1.0.1.2's client (its log: "build key 6AEB144C-2A3F000") and an older one.
const SIGS: [&str; 2] = ["sigs/6AEB144C-2A3F000.json", "sigs/6AE9B2AC-2A3F000.json"];

/// Creative Mode's folder of `version` (its DLL's bytes differ per version, as a real update's do).
fn creative(t: &Path, id: &str, version: &str) -> PathBuf {
    let d = t.join("src").join(format!("{id}-{version}"));
    put(&d, "mod.json", format!(r#"{{"id":"{id}","name":"Creative Mode","version":"{version}"}}"#).as_bytes());
    put(&d, "flame.json", br#"{"dll":"creative.dll"}"#);
    put(&d, "creative.dll", &build_test_dll(&["FlameStartRuntime"], version.as_bytes()));
    put(&d, "items.json", b"{\"groups\":[]}");
    for s in SIGS {
        put(&d, s, format!("signatures {s}").as_bytes());
    }
    put(&d, "symbols/client.json", b"recipes");
    d
}

fn import_into(dirs: &DataDirs, src: &Path) -> LibraryRef {
    let r = library::import(dirs, &[src.to_path_buf()], &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.kind == ModKind::Flame).cloned().unwrap_or_else(|| panic!("{r:?}"))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

fn game(t: &Path, mode: PathMode) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    if mode == PathMode::Z {
        std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    }
    GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: mode, source: "manual".into(),
    }
}

/// A runtime bundle in the launcher's data folder and the game's install receipt naming it; `reader`: the profile.json
/// reader revision its kiln-runtime.json declares (None: a runtime from before the declaration).
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
    let core = format!("core {git}");
    put(&g.path, "kiln/kiln-core.dll", core.as_bytes());
    put(&g.path, "kiln/.install.json", serde_json::to_string_pretty(&serde_json::json!({
        "format": 1, "installId": "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b", "installed": "2026-11-11T00:00:00Z",
        "runtime": { "version": b.version, "components": ["core"] }, "side": "client",
        "files": [{ "path": "kiln/kiln-core.dll", "sha256": hash::sha256_hex(core.as_bytes()), "component": "core" }],
    })).unwrap().as_bytes());
}

fn z(p: &Path) -> String {
    format!("Z:{}", p.display()).replace('/', "\\")
}

fn activate_with(dirs: &DataDirs, g: &GameInstall, pid: &str, mods: Vec<ProfileMod>) -> profile::ProfileFile {
    let mut p = profile::get(dirs, pid).unwrap();
    p.mods.flame = mods;
    profile::save(dirs, &p).unwrap();
    profile::activate(dirs, pid, g).unwrap();
    profile::read_game_profile(&g.path).unwrap().unwrap()
}

/// What Creative Mode writes into `<game>\mods\creative\` while it runs (its log, its notice, its settings).
fn the_mod_writes(dir: &Path) {
    put(dir, "logs/creative.log", b"[14:23:41.375] [info] Creative Mode 3.2.0 loaded\n");
    put(dir, "logs/flame.log", b"[14:23:41.373] [info] Flame runtime started (SDK 1)\n");
    put(dir, "notice.txt", b"This message shows once for this game build.\n");
    put(dir, "settings.json", b"{\"menuKey\":\"F7\"}");
}

#[test]
fn creative_mode_runs_from_mods_in_the_game_folder_and_keeps_what_it_wrote_across_updates() {
    let t = tmp("placed");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
    // the mod looks for sigs\<build key>.json in <game>\mods\creative\: Kiln's copy is there, and the profile names it
    let copy = g.path.join("mods/creative");
    assert_eq!(file.mods.flame[0].path, z(&copy), "the profile names the game-folder copy");
    for f in ["flame.json", "creative.dll", "items.json", "symbols/client.json"].into_iter().chain(SIGS) {
        assert!(copy.join(f).is_file(), "{f} in {}", copy.display());
    }
    assert_eq!(engine_mods::marker(&copy).map(|m| m.version), Some("3.2.0".into()));
    // the game runs: the mod writes its log, notice and settings into its folder
    the_mod_writes(&copy);
    let lib = library::path_of(&dirs, &v320).unwrap();
    assert_eq!(hash::dirhash(&lib).unwrap(), v320.hash, "the Library entry stays as imported");
    assert!(profile::refresh_active(&dirs, &g).unwrap().is_none(), "nothing to redo before the next start");

    // an update: the new version replaces the copy, what the mod wrote stays
    let v321 = import_into(&dirs, &creative(&t.0, "creative", "3.2.1"));
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v321, true)]);
    assert_eq!(file.mods.flame[0].path, z(&copy));
    assert!(std::fs::read_to_string(copy.join("mod.json")).unwrap().contains("3.2.1"));
    assert_eq!(std::fs::read(copy.join("creative.dll")).unwrap(),
        std::fs::read(library::path_of(&dirs, &v321).unwrap().join("creative.dll")).unwrap());
    assert_eq!(std::fs::read(copy.join("settings.json")).unwrap(), b"{\"menuKey\":\"F7\"}", "the settings survive the update");
    assert!(copy.join("logs/creative.log").is_file() && copy.join("notice.txt").is_file());

    // switched off: the copy goes (kiln-flame's drop-in scan and Flame's loader run anything in mods\), its files wait
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v321, false)]);
    assert!(!copy.exists(), "a switched-off mod is not left where a loader runs it");
    assert!(!file.mods.flame[0].enabled);
    let kept = g.path.join("kiln/kept-mod-files/flame/creative");
    assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), b"{\"menuKey\":\"F7\"}");
    assert!(kept.join("logs/creative.log").is_file() && kept.join("notice.txt").is_file());
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v321, true)]);
    assert_eq!(file.mods.flame[0].path, z(&copy));
    assert_eq!(std::fs::read(copy.join("settings.json")).unwrap(), b"{\"menuKey\":\"F7\"}", "and come back with it");
    assert!(copy.join("logs/flame.log").is_file());
}

/// The player's game folder today: Kiln (dev.23 and the dev.24 build of 2026-11-11) ran Creative Mode from the Library,
/// and the mod made `<game>\mods\creative\` and wrote only its logs and its notice there, no flame.json. That folder
/// is no mod (nothing in it runs) and its files are the player's: Kiln places its copy in it, and the files stay in it
/// as files the mod wrote (they go to kept-mod-files with the copy's removal, never away).
#[test]
fn the_folder_creative_mode_wrote_under_an_older_kiln_takes_kilns_copy_and_keeps_its_files() {
    let t = tmp("leftover");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let copy = g.path.join("mods/creative");
    put(&copy, "logs/creative.log", b"No symbol file for this game build\n");
    put(&copy, "logs/flame.log", b"Flame runtime started (SDK 1)\n");
    put(&copy, "notice.txt", b"Enshrouded was updated: mods waiting for an update\n");
    let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.flame = vec![pm(&v320, true)];
    profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &pid, &g).unwrap();
    let file = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(file.mods.flame[0].path, z(&copy), "the profile names the copy in the mod's own folder: {a:?}");
    assert!(copy.join(SIGS[0]).is_file() && copy.join("flame.json").is_file(), "the mod's files are where it looks for them");
    assert_eq!(std::fs::read(copy.join("logs/creative.log")).unwrap(), b"No symbol file for this game build\n");
    assert_eq!(std::fs::read(copy.join("notice.txt")).unwrap(), b"Enshrouded was updated: mods waiting for an update\n");
    assert!(copy.join("logs/flame.log").is_file());
    assert!(a.kept_files.iter().any(|s| s.starts_with("flame:creative: ") && s.contains("notice.txt")), "the player is told: {a:?}");
    assert!(a.retained.is_empty(), "{a:?}");
    // they are the player's, not Kiln's: removing the mod keeps them
    let file = activate_with(&dirs, &g, &pid, vec![]);
    assert!(file.mods.flame.is_empty() && !copy.exists());
    let kept = g.path.join("kiln/kept-mod-files/flame/creative");
    assert_eq!(std::fs::read(kept.join("logs/creative.log")).unwrap(), b"No symbol file for this game build\n");
    assert_eq!(std::fs::read(kept.join("notice.txt")).unwrap(), b"Enshrouded was updated: mods waiting for an update\n");
}

/// A folder in mods\ with anything a loader could run is not one the mod only wrote into: it stays the player's.
#[test]
fn a_creative_mode_folder_the_player_put_in_mods_stays_theirs() {
    for (what, extra) in [("a hand install", None), ("a folder with a DLL", Some(("tool.dll", &b"MZ"[..]))),
                          ("a folder with an EML manifest", Some(("mod.json", &br#"{"id":"creative"}"#[..]))),
                          ("an Enshwick data pack", Some(("pack.toml", &b"id = \"creative\""[..])))] {
        let t = tmp("own");
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game(&t.0, PathMode::Z);
        let own = g.path.join("mods/creative");
        match extra {
            None => {
                let src = creative(&t.0, "creative", "3.1.0");
                for e in walk(&src) { put(&own, &e, &std::fs::read(src.join(&e)).unwrap()); }
            }
            Some((name, data)) => {
                put(&own, "notice.txt", b"x");
                put(&own, name, data);
            }
        }
        let before = hash::dirhash(&own).unwrap();
        let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
        let pid = profile::create(&dirs, "Creative", None).unwrap().id;
        let file = activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
        assert_eq!(hash::dirhash(&own).unwrap(), before, "{what}: never touched");
        assert!(engine_mods::marker(&own).is_none(), "{what}");
        assert_eq!(file.mods.flame[0].path, z(&library::path_of(&dirs, &v320).unwrap()), "{what}: the Library copy, as before");
    }
}

/// A data-only package still belongs to the player, even without a DLL or Lua file.
#[test]
fn a_folder_with_another_engines_manifest_is_never_adopted_as_flame_output() {
    for name in ["kiln.mod.json", "native.json", "KILN.MOD.JSON", "NATIVE.JSON"] {
        let t = tmp("foreign-manifest");
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game(&t.0, PathMode::Z);
        let own = g.path.join("mods/creative");
        put(&own, name, br#"{"id":"players-package","version":"1.0.0"}"#);
        put(&own, "data/items.json", b"player data");
        let before = hash::dirhash(&own).unwrap();
        let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
        let pid = profile::create(&dirs, "Creative", None).unwrap().id;
        activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
        assert!(engine_mods::marker(&own).is_none(), "{name}: a player's package must not become Kiln's copy");
        assert_eq!(hash::dirhash(&own).unwrap(), before, "{name}: the folder stays untouched");
        activate_with(&dirs, &g, &pid, vec![]);
        assert_eq!(hash::dirhash(&own).unwrap(), before, "{name}: switching off leaves it in place");
    }
}

fn walk(root: &Path) -> Vec<String> {
    let mut out = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(d) = stack.pop() {
        for e in std::fs::read_dir(&d).unwrap().flatten() {
            if e.path().is_dir() { stack.push(e.path()); continue; }
            out.push(e.path().strip_prefix(root).unwrap().to_string_lossy().into_owned());
        }
    }
    out
}

/// Copy mode names `../mods/creative` only for a core that reads that form in mods.flame (profileReader 3): every
/// core before calls such a profile invalid and loads no mods at all, so an older one gets the copy under kiln/profiles/
/// (its kiln-flame runs that DLL, and the mod still finds its files in Kiln's copy in mods\creative).
#[test]
fn copy_mode_names_the_copy_only_for_a_runtime_that_reads_it() {
    let t = tmp("copy-mode");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Copy);
    let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    let under_kiln = format!("profiles/{pid}/flame/creative");
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
    assert_eq!(file.mods.flame[0].path, under_kiln, "no receipt: the runtime is not known");
    assert!(g.path.join("mods/creative").join(SIGS[0]).is_file(), "placed all the same: the mod reads its files there");
    for (reader, git) in [(None, "e3d6185"), (Some(2), "a633659")] {
        runtime_in_game(&dirs, &g, reader, git);
        let file = activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
        assert_eq!(file.mods.flame[0].path, under_kiln, "reader {reader:?} reads ../mods/<folder> only in mods.eml");
        assert!(g.path.join("kiln").join(&under_kiln).join("flame.json").is_file());
    }
    runtime_in_game(&dirs, &g, Some(3), "b8f1e20");
    let file = activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
    assert_eq!(file.mods.flame[0].path, "../mods/creative");
    let a = profile::activate(&dirs, &pid, &g).unwrap();
    assert!(!g.path.join("kiln").join(&under_kiln).exists(), "no unused copy under kiln/profiles/");
    assert_eq!(a.copied, 0, "{a:?}");
    // the core's own parser reads it (a host build of native/core/src/profile.cpp)
    let pj = g.path.join("kiln/profile.json");
    let script = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../native/core/tests/test-profile.sh");
    let out = std::process::Command::new("bash").arg(&script).arg(&pj).env("TMPDIR", &t.0).output()
        .expect("bash runs native/core/tests/test-profile.sh (a C++20 compiler, c++ or $CXX, builds the parser)");
    let text = format!("{}{}", String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
    assert!(out.status.success() && text.contains("launcher profile ok"), "the native parser refused it:\n{text}");
}

#[test]
fn an_id_that_is_no_folder_name_is_not_placed_and_said_so() {
    let t = tmp("unsafe");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let bad = import_into(&dirs, &creative(&t.0, "nul", "1.0.0"));
    assert!(bad.id == "nul" && !engine_mods::is_game_folder_name(&bad.id));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    let mut p = profile::get(&dirs, &pid).unwrap();
    p.mods.flame = vec![pm(&bad, true)];
    profile::save(&dirs, &p).unwrap();
    let a = profile::activate(&dirs, &pid, &g).unwrap();
    assert!(a.warnings.iter().any(|w| w.starts_with("flame:nul: not placed: ")), "{a:?}");
    assert!(!g.path.join("mods/nul").exists());
    let file = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(file.mods.flame[0].path, z(&library::path_of(&dirs, &bad).unwrap()));
}

/// "No profile" (deactivate) and uninstall (unplace) stop the profile's Flame mods: the copy goes, the files the mod
/// wrote wait in kiln/kept-mod-files/flame/<id>/. A copy whose shipped files the player changed stays, reported.
#[test]
fn no_profile_stops_flame_mods_and_keeps_what_they_wrote() {
    for (uninstall, shipped) in [(false, false), (true, false), (false, true), (true, true)] {
        let t = tmp(&format!("cleanup-{uninstall}-{shipped}"));
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game(&t.0, PathMode::Z);
        let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
        let pid = profile::create(&dirs, "Creative", None).unwrap().id;
        activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
        let copy = g.path.join("mods/creative");
        the_mod_writes(&copy);
        if shipped { put(&copy, "items.json", b"edited by the player"); }
        let report = if uninstall { engine_mods::unplace(&g.path).unwrap() } else { profile::deactivate_report(&dirs, &g).unwrap() };
        if shipped {
            assert!(report.retained.iter().any(|r| r.path == copy && r.may_run), "{uninstall}/{shipped}: {report:?}");
            assert_eq!(std::fs::read(copy.join("items.json")).unwrap(), b"edited by the player");
            continue;
        }
        assert!(!copy.exists() && report.removed.contains(&copy), "{uninstall}: the copy no longer runs: {report:?}");
        let kept = g.path.join("kiln/kept-mod-files/flame/creative");
        assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), b"{\"menuKey\":\"F7\"}");
        assert!(kept.join("logs/creative.log").is_file() && kept.join("notice.txt").is_file());
    }
}

/// A profile.json an older launcher wrote names the Library entry, and nothing is in mods\creative yet: the first
/// launcher start (the watcher's refresh) places the copy and writes the file again to name it.
#[test]
fn a_profile_json_naming_the_library_entry_is_written_again_once_the_copy_is_placed() {
    let t = tmp("older-file");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
    let copy = g.path.join("mods/creative");
    std::fs::remove_dir_all(&copy).unwrap();
    std::fs::remove_file(g.path.join("kiln/state/library-placements.json")).unwrap();
    let file = g.path.join("kiln/profile.json");
    let mut f: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
    f["mods"]["flame"][0]["path"] = z(&library::path_of(&dirs, &v320).unwrap()).into();
    std::fs::write(&file, serde_json::to_vec(&f).unwrap()).unwrap();
    profile::refresh_active(&dirs, &g).unwrap();
    assert!(copy.join(SIGS[0]).is_file());
    assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().mods.flame[0].path, z(&copy));
}

#[test]
fn play_once_keeps_the_active_selection() {
    let t = tmp("once");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game(&t.0, PathMode::Z);
    let v320 = import_into(&dirs, &creative(&t.0, "creative", "3.2.0"));
    let pid = profile::create(&dirs, "Creative", None).unwrap().id;
    activate_with(&dirs, &g, &pid, vec![pm(&v320, true)]);
    let same = profile::create(&dirs, "Same", None).unwrap();
    let mut p = profile::get(&dirs, &same.id).unwrap();
    p.mods.flame = vec![pm(&v320, true)];
    profile::save(&dirs, &p).unwrap();
    let once = profile::write_once(&dirs, &same.id, &g).unwrap();
    assert_eq!(once.mods.flame[0].path, z(&g.path.join("mods/creative")));
    let none = profile::create(&dirs, "None", None).unwrap();
    let e = profile::write_once(&dirs, &none.id, &g).unwrap_err();
    assert_eq!(e.code, kiln_manage::ErrorCode::Unsupported, "{e:?}");
    assert!(e.message.contains("Flame"), "{}", e.message);
}
