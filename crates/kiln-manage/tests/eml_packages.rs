//! EML mods as Nexus ships them (the path lists of the 2026-11-10 survey, built by hand with stand-in files), dropped on
//! the launcher (quick::add): what lands in the Library, in the profile, in the game's profile.json and in the game
//! folder. Includes the packages that carry a native DLL next to their EML mod (Auto Loot Plus, the XHL family).
#![cfg(all(unix, feature = "zip"))]

use std::io::Write;

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    static N: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
    let n = N.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    let p = std::env::temp_dir().join(format!("km-eml-packages-{tag}-{}-{n}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}
/// The files of `dir` as a zip (entry names as in the Nexus path list).
fn zip_of(dir: &Path, out: &Path) {
    let mut w = zip::ZipWriter::new(std::fs::File::create(out).unwrap());
    let mut stack = vec![dir.to_path_buf()];
    let mut files = Vec::new();
    while let Some(d) = stack.pop() {
        for e in std::fs::read_dir(&d).unwrap() {
            let p = e.unwrap().path();
            if p.is_dir() { stack.push(p) } else { files.push(p) }
        }
    }
    files.sort();
    for f in files {
        let name = f.strip_prefix(dir).unwrap().to_string_lossy().replace('\\', "/");
        w.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
        w.write_all(&std::fs::read(&f).unwrap()).unwrap();
    }
    w.finish().unwrap();
}

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, ImportReport};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile;
use kiln_manage::task::NoProgress;
use std::path::{Path, PathBuf};

fn game_with_z(t: &Path) -> GameInstall {
    let game = t.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let pfx = t.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    GameInstall {
        id: "g-eml".into(), kind: GameKind::Client, path: game, exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx), build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into(),
    }
}

// Stand-ins with the markers Kiln classifies by (engines.rs): EML's loader holds EML_LOG_FILE_PATH; Auto Loot Plus's
// and XHL-Core's dinput8.dll are DirectInput8 proxies that read dinput8-chain.ini.
fn eml_dbghelp() -> Vec<u8> { build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH") }
fn eml_dinput8() -> Vec<u8> { build_test_dll(&["DirectInput8Create"], b"EML_LOG_FILE_PATH") }
fn alp_dinput8() -> Vec<u8> { build_test_dll(&["DirectInput8Create"], b"auto-loot-plus.log\0dinput8-chain.ini\0[Chain]\0mods\\auto-loot-plus\\config.txt") }
fn xhl_core() -> Vec<u8> { build_test_dll(&["DirectInput8Create"], b"XHL-Native-Plugin-Host.log\0dinput8-chain.ini\0native-plugin.ini") }
fn xhl_plugin() -> Vec<u8> { build_test_dll(&["XhlPluginEntry"], b"XHL plugin") }

fn eml_mod(root: &Path, dir: &str, id: &str) {
    put(root, &format!("{dir}/mod.json"), format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["patch"]}}"#).as_bytes());
    put(root, &format!("{dir}/src/mod.lua"), b"return {}");
}

/// The archive layouts, one per class.
fn layout(root: &Path, which: &str) {
    match which {
        // Auto Loot Plus 1.1.8 (the main file): its own dinput8.dll + the EML part in mods/auto-loot-plus
        "alp-full" => {
            put(root, "dinput8.dll", &alp_dinput8());
            put(root, "AutoLootPlus-README.txt", b"readme");
            put(root, "licenses/AutoLootPlus-LICENSE.txt", b"MIT");
            put(root, "licenses/DearImGui-LICENSE.txt", b"MIT");
            eml_mod(root, "mods/auto-loot-plus", "auto-loot-plus");
            put(root, "mods/auto-loot-plus/config.txt", b"radius=15");
            put(root, "mods/auto-loot-plus/quest_items.txt", b"x");
            put(root, "mods/auto-loot-plus/src/autosmash_program.lua", b"return {}");
        }
        // Auto Loot Plus Lite: the DLL and its settings file, no EML
        "alp-lite" => {
            put(root, "dinput8.dll", &alp_dinput8());
            put(root, "AutoLootPlus-README.txt", b"readme");
            put(root, "licenses/AutoLootPlus-LICENSE.txt", b"MIT");
            put(root, "mods/auto-loot-plus/config.txt", b"radius=15");
        }
        // XHL-Auto-Loot (and the other XHL mods of 2026-10): EML's dbghelp.dll + a folder that is an EML mod and an
        // XHL-Core plugin at once
        "xhl-hybrid" => {
            put(root, "dbghelp.dll", &eml_dbghelp());
            eml_mod(root, "mods/XHL-Auto-Loot", "XHL-Auto-Loot");
            put(root, "mods/XHL-Auto-Loot/XHL-Auto-Loot.dll", &xhl_plugin());
            put(root, "mods/XHL-Auto-Loot/native-plugin.ini", b"[Plugin]\nDll=XHL-Auto-Loot.dll\n");
            put(root, "mods/XHL-Auto-Loot/config.txt", b"x");
        }
        // older XHL packages: XHL-Core's dinput8.dll in the same archive
        "xhl-hybrid-core" => {
            layout(root, "xhl-hybrid");
            put(root, "dinput8.dll", &xhl_core());
        }
        // XHL-Core alone
        "xhl-core" => put(root, "dinput8.dll", &xhl_core()),
        // an XHL plugin without an EML part (XHL-Vein-Mining 2.x drops dbghelp.dll)
        "xhl-plugin" => {
            put(root, "mods/XHL-Vein-Mining/XHL-Vein-Mining.dll", &xhl_plugin());
            put(root, "mods/XHL-Vein-Mining/native-plugin.ini", b"[Plugin]\nDll=XHL-Vein-Mining.dll\n");
            put(root, "mods/XHL-Vein-Mining/config.txt", b"x");
        }
        // XHL-Max-Stack-65535 and the other pure XHL data mods: dbghelp.dll + mods/<Name>/ with config.txt
        "loader-dbghelp" => {
            put(root, "dbghelp.dll", &eml_dbghelp());
            eml_mod(root, "mods/XHL-Max-Stack-65535", "XHL-Max-Stack-65535");
            put(root, "mods/XHL-Max-Stack-65535/config.txt", b"65535");
        }
        // Hoarder's Helper "EML included", Vietnamese, Map Zoom: both EML proxies (+ eml.json, a licence)
        "loader-both" => {
            put(root, "dbghelp.dll", &eml_dbghelp());
            put(root, "dinput8.dll", &eml_dinput8());
            put(root, "eml.json", br#"{"enable_console":false}"#);
            put(root, "EML_LICENSE_GPL3.txt", b"GPL");
            eml_mod(root, "mods/map_zoom", "map_zoom");
        }
        // dev.21's Hoarder's Helper: the loader renamed dbghelp_<mod>.dll
        "loader-renamed" => {
            put(root, "dbghelp_HoardersHelper.dll", &eml_dbghelp());
            eml_mod(root, "mods/HoardersHelper", "HoardersHelper");
        }
        // mods/<id>/ without a loader (Hoarder's Helper "Mod Only", Zero Cost Build Camera); Minor KR Fixes: "Mods/"
        "mods-folder" => eml_mod(root, "Mods/Minor KR Fixes", "Minor KR Fixes"),
        // the mod folder at the root (Ember, Auto Loot, Max Stack, Chest Slots ...), with its settings file
        "mod-at-root" => {
            eml_mod(root, "quick-stack-range", "quick-stack-range");
            put(root, "quick-stack-range/config.ini", b"[range]\nsmall=32\n");
            put(root, "quick-stack-range/README.md", b"readme");
        }
        // a wrapper folder around mods/<id>/ (Enshrouded VietNamese NEW) or around <id>/ (M182 Fire Aura)
        "nested" => {
            eml_mod(root, "Enshrouded_Tieng_Viet_Update/mods/enshrouded_vi_update8", "enshrouded_vi_update8");
            eml_mod(root, "m182_fire_aura/m182_fire_aura", "m182_fire_aura");
        }
        _ => panic!("{which}"),
    }
}

fn zip(t: &Tmp, which: &str, name: &str) -> PathBuf {
    let d = t.0.join("pkg").join(name);
    layout(&d, which);
    let z = t.0.join(format!("{name}.zip"));
    zip_of(&d, &z);
    z
}

fn summary(r: &ImportReport) -> String {
    let mut out = Vec::new();
    for x in r.imported.iter().chain(&r.existing) { out.push(format!("+ {}/{}", x.kind.as_str(), x.id)); }
    for s in &r.skipped { out.push(format!("- {}: {}", Path::new(&s.path).file_name().unwrap().to_string_lossy(), s.reason)); }
    for n in &r.notes { out.push(format!("i {}", n.text)); }
    out.join("\n")
}

/// Files of the game folder outside kiln/ and the exe, '/' separated, sorted.
fn tree(dir: &Path) -> Vec<String> {
    let mut out = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for e in std::fs::read_dir(&d).into_iter().flatten().flatten() {
            let p = e.path();
            let rel = p.strip_prefix(dir).unwrap().to_string_lossy().into_owned();
            if rel == "kiln" || rel == "enshrouded.exe" { continue; }
            if p.is_dir() { stack.push(p) } else { out.push(rel) }
        }
    }
    out.sort();
    out
}

struct Dropped {
    _t: Tmp,
    dirs: DataDirs,
    g: GameInstall,
    report: ImportReport,
}

/// A fresh player (no profile yet) drops the package on the launcher window.
fn drop_on_launcher(which: &str, name: &str) -> Dropped {
    let t = tmp(&format!("drop-{which}"));
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    let z = zip(&t, which, name);
    let r = kiln_manage::quick::add_with(&dirs, &g, &[z], false, &mut NoProgress).unwrap();
    println!("=== {which}\n{}", summary(&r.import));
    Dropped { _t: t, dirs, g, report: r.import }
}

impl Dropped {
    fn found(&self) -> Vec<String> {
        let mut v: Vec<String> = self.report.imported.iter().chain(&self.report.existing).map(|x| format!("{}/{}", x.kind.as_str(), x.id)).collect();
        v.sort();
        v
    }
    /// The game's profile.json: `(id, path relative to the game when it is in the game folder, else "library")`.
    fn eml_paths(&self) -> Vec<(String, String)> {
        let f = profile::read_game_profile(&self.g.path).unwrap().expect("profile.json");
        let game = format!("Z:{}", self.g.path.display()).replace('/', "\\");
        f.mods.eml.iter().map(|m| (m.id.clone(), match m.path.strip_prefix(&game) {
            Some(rest) => rest.trim_start_matches('\\').replace('\\', "/"),
            None => { assert!(m.path.contains("\\data\\library\\eml\\"), "{}", m.path); "library".into() }
        })).collect()
    }
    fn natives(&self) -> Vec<String> {
        let f = profile::read_game_profile(&self.g.path).unwrap().expect("profile.json");
        f.mods.native.iter().filter(|m| m.enabled).map(|m| m.id.clone()).collect()
    }
    /// The `[profile]` lines of kiln/chain.ini (the dllmain natives Kiln's proxy chain-loads).
    fn chained(&self) -> Vec<String> {
        let text = std::fs::read_to_string(self.g.path.join("kiln/chain.ini")).unwrap_or_default();
        let mut on = false;
        text.lines().filter_map(|l| {
            let l = l.trim();
            if l.starts_with('[') { on = l.eq_ignore_ascii_case("[profile]"); return None; }
            (on && !l.is_empty() && !l.starts_with(';')).then(|| l.to_string())
        }).collect()
    }
    fn reasons(&self) -> String {
        self.report.skipped.iter().map(|s| s.reason.as_str()).chain(self.report.notes.iter().map(|n| n.text.as_str())).collect::<Vec<_>>().join("\n")
    }
}

const NOT_NEEDED: &str = "EML's own loader is in this package: not needed, Kiln runs EML mods";

/// Auto Loot Plus (Nexus 170, the most downloaded mod that needs EML): its dinput8.dll is the mod. Before dev.24 the
/// Library called it "a dinput8-chain.ini chain loader: not needed" and left it out, and the EML part ran from the
/// Library, where the DLL never finds mods/auto-loot-plus.
#[test]
fn auto_loot_plus_gets_its_dll_and_its_eml_part_in_the_game_folder() {
    let d = drop_on_launcher("alp-full", "AutoLootPlus 1.1.8");
    assert_eq!(d.found(), ["eml/auto-loot-plus", "native/autolootplus-1.1.8"]);
    assert!(!d.reasons().contains("not needed"), "{}", d.reasons());
    assert!(d.report.skipped.is_empty(), "{:?}", d.report.skipped);
    assert!(d.reasons().contains("its package's dinput8.dll reads it from the game's mods folder, so Kiln copies it into the game's mods\\auto-loot-plus folder"), "{}", d.reasons());
    // the DLL: a native mod named after the package, chain-loaded in DllMain like the game loaded it before
    assert_eq!(d.natives(), ["autolootplus-1.1.8"]);
    let lib = library::list(&d.dirs, Some(ModKind::Native)).unwrap();
    assert_eq!(lib[0].name, "AutoLootPlus 1.1.8");
    let native = lib[0].versions[0].folder.clone();
    let man: serde_json::Value = serde_json::from_slice(&std::fs::read(native.join("native.json")).unwrap()).unwrap();
    assert_eq!((man["phase"].as_str(), man["dlls"][0].as_str()), (Some("dllmain"), Some("dinput8.dll")));
    assert_eq!(d.chained().len(), 1, "chain.ini [profile]: {:?}", d.chained());
    assert!(d.chained()[0].to_ascii_lowercase().ends_with("dinput8.dll"), "{:?}", d.chained());
    // the EML part: in <game>/mods/auto-loot-plus, where the DLL reads and writes config.txt, and kiln-eml runs that copy
    assert_eq!(d.eml_paths(), [("auto-loot-plus".to_string(), "mods/auto-loot-plus".to_string())]);
    assert_eq!(tree(&d.g.path), ["mods/auto-loot-plus/.kiln-library.json", "mods/auto-loot-plus/config.txt", "mods/auto-loot-plus/mod.json",
        "mods/auto-loot-plus/quest_items.txt", "mods/auto-loot-plus/src/autosmash_program.lua", "mods/auto-loot-plus/src/mod.lua"]);
}

#[test]
fn auto_loot_plus_lite_gets_its_dll_and_its_settings_in_the_game_folder() {
    let d = drop_on_launcher("alp-lite", "AutoLootPlus Lite 1.1.8");
    assert_eq!(d.found(), ["native/autolootplus-lite-1.1.8"]);
    assert_eq!(d.natives(), ["autolootplus-lite-1.1.8"]);
    assert!(d.report.skipped.is_empty(), "{:?}", d.report.skipped);
    assert_eq!(std::fs::read(d.g.path.join("mods/auto-loot-plus/config.txt")).unwrap(), b"radius=15");
    assert!(d.eml_paths().is_empty(), "settings-only folders are not invented EML mods");
    assert!(!d.reasons().contains("not needed"), "{}", d.reasons());
}

#[test]
fn native_only_settings_are_owned_preserved_and_never_replace_a_manual_folder() {
    let d = drop_on_launcher("alp-lite", "AutoLootPlus Lite");
    let copy = d.g.path.join("mods/auto-loot-plus");
    std::fs::write(copy.join("config.txt"), b"radius=42").unwrap();
    profile::activate(&d.dirs, "my-mods", &d.g).unwrap();
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42");
    let mut p = my_mods(&d);
    p.mods.native[0].enabled = false;
    let mut p = profile::save(&d.dirs, &p).unwrap();
    assert!(profile::write_once(&d.dirs, &p.id, &d.g).is_err(), "play-once cannot leave stale native settings in the game");
    profile::activate(&d.dirs, &p.id, &d.g).unwrap();
    assert!(!copy.exists());
    p.mods.native[0].enabled = true;
    let mut p = activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42");
    p.mods.native.clear();
    let mut p = activate(&d, &p);
    assert!(!copy.exists());
    put(&copy, "config.txt", b"manual");
    // Restore the imported native selection without granting ownership of the manual replacement.
    let r = &d.report.imported[0];
    p.mods.native.push(profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true });
    activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"manual");
    assert!(!copy.join(".kiln-library.json").exists());
}

#[test]
fn mixed_native_settings_folders_keep_separate_player_values_across_updates() {
    let t = tmp("mixed-native-settings");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    let pkg = t.0.join("Native settings bundle");
    put(&pkg, "dinput8.dll", &alp_dinput8());
    eml_mod(&pkg, "mods/regular", "regular");
    put(&pkg, "mods/First/config.txt", b"first default");
    put(&pkg, "mods/Second/config.txt", b"second default");
    let r = kiln_manage::quick::add_with(&dirs, &g, &[pkg.clone()], false, &mut NoProgress).unwrap();
    assert!(r.import.skipped.is_empty(), "{r:?}");
    assert_eq!(r.import.imported.len(), 2);
    put(&g.path, "mods/First/config.txt", b"first player");
    put(&g.path, "mods/Second/config.txt", b"second player");
    put(&pkg, "mods/First/config.txt", b"new default");
    kiln_manage::quick::add_with(&dirs, &g, &[pkg], false, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(g.path.join("mods/First/config.txt")).unwrap(), b"first player");
    let mut p = profile::get(&dirs, "my-mods").unwrap();
    p.mods.native[0].enabled = false;
    let mut p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &g).unwrap();
    assert!(!g.path.join("mods/First").exists() && !g.path.join("mods/Second").exists());
    p.mods.native[0].enabled = true;
    profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &g).unwrap();
    assert_eq!(std::fs::read(g.path.join("mods/First/config.txt")).unwrap(), b"first player");
    assert_eq!(std::fs::read(g.path.join("mods/Second/config.txt")).unwrap(), b"second player");
}

/// XHL-Auto-Loot and the other XHL mods of 2026-10: one folder is an EML mod and an XHL-Core plugin at once; XHL-Core
/// loads the plugin from <game>/mods/<name>.
#[test]
fn xhl_mods_are_placed_where_xhl_core_finds_their_plugin() {
    for (which, natives) in [("xhl-hybrid", vec![]), ("xhl-hybrid-core", vec!["xhl-auto-loot-1.5.0".to_string()])] {
        let d = drop_on_launcher(which, "XHL-Auto-Loot 1.5.0");
        assert_eq!(d.eml_paths(), [("xhl-auto-loot".to_string(), "mods/XHL-Auto-Loot".to_string())], "{which}");
        assert!(tree(&d.g.path).contains(&"mods/XHL-Auto-Loot/XHL-Auto-Loot.dll".to_string()), "{which}: {:?}", tree(&d.g.path));
        assert!(tree(&d.g.path).contains(&"mods/XHL-Auto-Loot/native-plugin.ini".to_string()));
        assert!(d.reasons().contains(NOT_NEEDED), "{which}: EML's dbghelp.dll is still left out: {}", d.reasons());
        assert!(d.reasons().contains("XHL-Core plugin (native-plugin.ini)"), "{}", d.reasons());
        assert_eq!(d.natives(), natives, "{which}: XHL-Core in the package is a native mod");
    }
}

#[test]
fn xhl_core_alone_is_native_but_plain_plugins_are_placed_without_being_executed_by_kiln() {
    let d = drop_on_launcher("xhl-core", "XHL-Core");
    assert_eq!(d.found(), ["native/xhl-core"]);
    assert_eq!(d.natives(), ["xhl-core"]);
    assert!(d.reasons().is_empty(), "{}", d.reasons());
    let d = drop_on_launcher("xhl-plugin", "XHL-Vein-Mining");
    assert_eq!(d.found(), ["xhl/xhl-vein-mining"]);
    assert!(d.report.skipped.is_empty(), "{:?}", d.report.skipped);
    assert!(d.natives().is_empty() && d.eml_paths().is_empty() && d.chained().is_empty());
    assert_eq!(std::fs::read(d.g.path.join("mods/XHL-Vein-Mining/XHL-Vein-Mining.dll")).unwrap(), xhl_plugin());
    assert!(d.reasons().contains("requires XHL-Core") && d.reasons().contains("never loads this plugin DLL itself"), "{}", d.reasons());
}

/// The plain EML packages keep running from the Library: no copy in the game folder, the bundled loader left out.
#[test]
fn plain_eml_packages_run_from_the_library() {
    for (which, ids, loader) in [
        ("loader-dbghelp", vec!["xhl-max-stack-65535"], true),
        ("loader-both", vec!["map_zoom"], true),
        ("loader-renamed", vec!["hoardershelper"], true),
        ("mods-folder", vec!["minor-kr-fixes"], false),
        ("mod-at-root", vec!["quick-stack-range"], false),
        ("nested", vec!["enshrouded_vi_update8", "m182_fire_aura"], false),
    ] {
        let d = drop_on_launcher(which, which);
        let want: Vec<(String, String)> = ids.iter().map(|i| (i.to_string(), "library".to_string())).collect();
        assert_eq!(d.eml_paths(), want, "{which}");
        assert!(d.natives().is_empty() && tree(&d.g.path).is_empty(), "{which}: {:?}", tree(&d.g.path));
        assert!(d.report.skipped.is_empty(), "{which}: {:?}", d.report.skipped);
        assert_eq!(d.reasons().contains(NOT_NEEDED), loader, "{which}: {}", d.reasons());
    }
}

fn my_mods(d: &Dropped) -> profile::Profile {
    profile::get(&d.dirs, "my-mods").unwrap()
}

fn activate(d: &Dropped, p: &profile::Profile) -> profile::Profile {
    let saved = profile::save(&d.dirs, p).unwrap();
    profile::activate(&d.dirs, &p.id, &d.g).unwrap();
    saved
}

/// The DLL writes its settings into the copy (Auto Loot Plus's F8 menu saves mods/auto-loot-plus/config.txt): an update
/// of the mod keeps them, switching the mod off keeps them aside and removes the copy, switching it on brings them back.
#[test]
fn the_dlls_settings_survive_updates_and_switching_off() {
    let d = drop_on_launcher("alp-full", "AutoLootPlus 1.1.8");
    let copy = d.g.path.join("mods/auto-loot-plus");
    std::fs::write(copy.join("config.txt"), b"radius=42").unwrap();
    std::fs::write(copy.join("ignored_items.txt"), b"twig").unwrap();
    // activating again keeps the copy as it is
    profile::activate(&d.dirs, "my-mods", &d.g).unwrap();
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42");
    // 1.1.9: new Lua and a new default config
    let t = tmp("alp-119");
    let pkg = t.0.join("pkg");
    layout(&pkg, "alp-full");
    put(&pkg, "mods/auto-loot-plus/mod.json", br#"{"id":"auto-loot-plus","name":"Auto Loot Plus","version":"1.1.9","capabilities":["patch"]}"#);
    put(&pkg, "mods/auto-loot-plus/src/mod.lua", b"-- 1.1.9\nreturn {}");
    put(&pkg, "mods/auto-loot-plus/config.txt", b"radius=15\nnewKey=1");
    let z = t.0.join("AutoLootPlus 1.1.9.zip");
    zip_of(&pkg, &z);
    let r = kiln_manage::quick::add_with(&d.dirs, &d.g, &[z], false, &mut NoProgress).unwrap();
    assert!(r.applied, "{r:?}");
    assert_eq!(std::fs::read(copy.join("src/mod.lua")).unwrap(), b"-- 1.1.9\nreturn {}", "the new version is placed");
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42", "the player's settings stay");
    assert_eq!(std::fs::read(copy.join("ignored_items.txt")).unwrap(), b"twig");
    // switched off: the copy goes (the DLL no longer finds a mod the profile does not run), its settings wait
    let mut p = my_mods(&d);
    p.mods.eml.iter_mut().for_each(|m| m.enabled = false);
    let mut p = activate(&d, &p);
    assert!(!copy.exists());
    // switched on again: the settings come back
    p.mods.eml.iter_mut().for_each(|m| m.enabled = true);
    let mut p = activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42");
    assert_eq!(std::fs::read(copy.join("src/mod.lua")).unwrap(), b"-- 1.1.9\nreturn {}");
    assert!(!d.g.path.join("kiln/state/placed-settings/eml/auto-loot-plus").exists());
    // gone from the profile: the copy goes
    p.mods.eml.clear();
    activate(&d, &p);
    assert!(!copy.exists() && tree(&d.g.path).is_empty(), "{:?}", tree(&d.g.path));
}

/// A player who installed the mod by hand (mods/auto-loot-plus without Kiln's marker) keeps that folder: Kiln never
/// writes into it, and the profile's entry names it, so kiln-eml runs it once.
#[test]
fn a_hand_installed_folder_is_the_players() {
    let t = tmp("by-hand");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    eml_mod(&g.path, "mods/auto-loot-plus", "auto-loot-plus");
    put(&g.path, "mods/auto-loot-plus/config.txt", b"by hand");
    let z = zip(&t, "alp-full", "AutoLootPlus 1.1.8");
    kiln_manage::quick::add_with(&dirs, &g, &[z], false, &mut NoProgress).unwrap();
    assert!(kiln_manage::engine_mods::marker(&g.path.join("mods/auto-loot-plus")).is_none());
    assert_eq!(std::fs::read(g.path.join("mods/auto-loot-plus/config.txt")).unwrap(), b"by hand");
    let p = profile::get(&dirs, "my-mods").unwrap();
    let rep = kiln_manage::engine_mods::place(&dirs, &p, &g.path).unwrap();
    assert_eq!(rep.own, ["eml:auto-loot-plus"]);
}

/// EML's own dbghelp.dll in the game folder (Windows loads it): every EML mod of the profile is in mods/ for it, the
/// game-folder ones under their folder name, and the launcher says all of them run under it.
#[test]
fn with_emls_own_loader_installed_every_mod_is_in_its_folder() {
    let t = tmp("own-loader");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    put(&g.path, "dbghelp.dll", &eml_dbghelp());
    let zips = [zip(&t, "xhl-hybrid", "XHL-Auto-Loot"), zip(&t, "loader-dbghelp", "XHL-Max-Stack"), zip(&t, "alp-full", "AutoLootPlus 1.1.8")];
    let r = kiln_manage::quick::add_with(&dirs, &g, &zips, false, &mut NoProgress).unwrap();
    assert!(r.applied, "{r:?}");
    let files = tree(&g.path);
    for f in ["mods/XHL-Auto-Loot/.kiln-library.json", "mods/xhl-max-stack-65535/.kiln-library.json", "mods/auto-loot-plus/.kiln-library.json"] {
        assert!(files.contains(&f.to_string()), "{f}: {files:?}");
    }
    let a = r.activation.expect("activated");
    let note = a.own_loaders.iter().find(|n| n.loader.kind == ModKind::Eml).expect("EML's own loader note");
    assert_eq!(note.copied.len(), 3, "{note:?}");
    assert!(note.not_copied.is_empty(), "{note:?}");
    // the loader goes: the plain mod's copy goes too (kiln-eml runs it from the Library), the game-folder ones stay
    std::fs::remove_file(g.path.join("dbghelp.dll")).unwrap();
    profile::activate(&dirs, "my-mods", &g).unwrap();
    let files = tree(&g.path);
    assert!(!files.iter().any(|f| f.starts_with("mods/xhl-max-stack-65535/")), "{files:?}");
    assert!(files.contains(&"mods/XHL-Auto-Loot/native-plugin.ini".to_string()) && files.contains(&"mods/auto-loot-plus/mod.json".to_string()));
}

/// A Library entry imported by an older Kiln (no gameFolder in its record) learns it when the same archive is dropped
/// again: the player only has to drop it once more after updating.
#[test]
fn an_older_import_learns_its_game_folder_from_the_same_archive() {
    let d = drop_on_launcher("alp-full", "AutoLootPlus 1.1.8");
    let lib = library::list(&d.dirs, Some(ModKind::Eml)).unwrap();
    let rec_path = PathBuf::from(format!("{}.json", lib[0].versions[0].folder.display()));
    let mut rec: serde_json::Value = serde_json::from_slice(&std::fs::read(&rec_path).unwrap()).unwrap();
    assert_eq!(rec["gameFolder"], "auto-loot-plus");
    rec.as_object_mut().unwrap().remove("gameFolder");
    std::fs::write(&rec_path, serde_json::to_vec(&rec).unwrap()).unwrap();
    profile::activate(&d.dirs, "my-mods", &d.g).unwrap();
    assert_eq!(d.eml_paths()[0].1, "library", "without the record's gameFolder it runs from the Library, as before");
    let pkg = d._t.0.join("again");
    layout(&pkg, "alp-full");
    std::fs::create_dir_all(d._t.0.join("again-zip")).unwrap();
    let z = d._t.0.join("again-zip/AutoLootPlus 1.1.8.zip");
    zip_of(&pkg, &z);
    let r = kiln_manage::quick::add_with(&d.dirs, &d.g, &[z], false, &mut NoProgress).unwrap();
    assert_eq!(r.import.existing.len(), 2, "{:?}", r.import);
    profile::activate(&d.dirs, "my-mods", &d.g).unwrap();
    assert_eq!(d.eml_paths()[0].1, "mods/auto-loot-plus");
}

#[test]
fn native_bundle_companions_and_chain_targets_stay_with_the_proxy() {
    let t = tmp("native-companions");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    let pkg = t.0.join("pkg/Wrapper");
    layout(&pkg, "alp-full");
    put(&pkg, "dbghelp_mod.dll", &eml_dbghelp());
    put(&pkg, "helper.dll", &build_test_dll(&["Helper"], b"dependency"));
    put(&pkg, "extra/other.dll", &build_test_dll(&["Other"], b"chain target"));
    put(&pkg, "native-config.ini", b"on=1");
    put(&pkg, "dinput8-chain.ini", b"[Chain]\nextra/other.dll\ndbghelp_mod.dll\n");
    let z = t.0.join("Native companions.zip");
    zip_of(&t.0.join("pkg"), &z);
    let r = kiln_manage::quick::add_with(&dirs, &g, &[z], false, &mut NoProgress).unwrap();
    let natives = library::list(&dirs, Some(ModKind::Native)).unwrap();
    assert_eq!(natives.len(), 1, "helpers must not become independent mods: {r:?}");
    let folder = &natives[0].versions[0].folder;
    for f in ["dinput8.dll", "helper.dll", "native-config.ini", "dinput8-chain.ini", "extra/other.dll"] {
        assert!(folder.join(f).is_file(), "missing companion {f}: {r:?}");
    }
    assert!(!folder.join("dbghelp_mod.dll").exists(), "the redundant EML loader stays skipped");
    assert!(!folder.join("mods").exists(), "the EML part is a separate Library mod");
    let man = kiln_manage::manifest::read_native(folder).unwrap();
    assert_eq!(man.dlls, ["dinput8.dll", "extra/other.dll"], "the package chain is translated, not discarded");
    assert_eq!(r.import.imported.len(), 2, "{r:?}");
    let chain = std::fs::read_to_string(g.path.join("kiln/chain.ini")).unwrap();
    assert!(chain.contains("extra\\other.dll"), "{chain}");
}

#[test]
fn a_native_only_package_translates_its_chain_without_touching_the_players_chain() {
    let t = tmp("native-only-chain");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    put(&g.path, "dinput8-chain.ini", b"; player's chain\n");
    let pkg = t.0.join("Native bundle");
    put(&pkg, "dinput8.dll", &alp_dinput8());
    put(&pkg, "extra/other.dll", &build_test_dll(&["Other"], b"chain target"));
    put(&pkg, "dinput8-chain.ini", b"[Chain]\nextra/other.dll\n../outside.dll\n");
    let r = kiln_manage::quick::add_with(&dirs, &g, &[pkg], false, &mut NoProgress).unwrap();
    let lib = library::list(&dirs, Some(ModKind::Native)).unwrap();
    assert_eq!(lib.len(), 1, "{r:?}");
    let man = kiln_manage::manifest::read_native(&lib[0].versions[0].folder).unwrap();
    assert_eq!(man.dlls, ["dinput8.dll", "extra/other.dll"]);
    assert_eq!(std::fs::read(g.path.join("dinput8-chain.ini")).unwrap(), b"; player's chain\n");
    assert!(r.import.skipped.iter().any(|s| s.reason.contains("unsafe DLL path")), "{r:?}");
}

#[test]
fn changed_code_is_never_discarded_as_native_settings() {
    let d = drop_on_launcher("alp-full", "AutoLootPlus 1.1.8");
    let copy = d.g.path.join("mods/auto-loot-plus");
    std::fs::write(copy.join("src/mod.lua"), b"-- player's code").unwrap();
    let mut p = my_mods(&d);
    p.mods.eml.clear();
    activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("src/mod.lua")).unwrap(), b"-- player's code");
    assert_eq!(kiln_manage::engine_mods::place(&d.dirs, &p, &d.g.path).unwrap().changed, ["eml:auto-loot-plus"]);
}

#[test]
fn direct_mod_folders_and_archive_roots_keep_their_data() {
    let t = tmp("direct");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    let direct = t.0.join("Direct Mod");
    eml_mod(&direct, ".", "Direct Mod");
    put(&direct, "data/values.json", b"[1,2]");
    let root_zip = t.0.join("Root mod.zip");
    zip_of(&direct, &root_zip);
    let r = kiln_manage::quick::add_with(&dirs, &g, &[direct, root_zip], false, &mut NoProgress).unwrap();
    assert_eq!((r.import.imported.len(), r.import.existing.len()), (1, 1), "{r:?}");
    let f = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(f.mods.eml.len(), 1);
    let lib = library::list(&dirs, Some(ModKind::Eml)).unwrap();
    assert!(lib[0].versions[0].folder.join("data/values.json").is_file());
}

#[test]
fn a_manual_copy_under_another_name_is_the_actual_profile_path() {
    let t = tmp("manual-other-name");
    let dirs = DataDirs::at(t.0.join("data"));
    let g = game_with_z(&t.0);
    eml_mod(&g.path, "mods/My Auto Loot", "auto-loot-plus");
    let z = zip(&t, "alp-full", "AutoLootPlus");
    kiln_manage::quick::add_with(&dirs, &g, &[z], false, &mut NoProgress).unwrap();
    assert!(!g.path.join("mods/auto-loot-plus").exists(), "do not make a duplicate");
    let f = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert!(f.mods.eml[0].path.ends_with("mods\\My Auto Loot"), "{f:?}");
}

#[test]
fn game_folder_cleanup_uses_native_receipts_without_adopting_player_settings() {
    for which in ["alp-full", "alp-lite"] {
        for edited in [false, true] {
            let d = drop_on_launcher(which, "Cleanup package");
            let copy = d.g.path.join("mods/auto-loot-plus");
            if edited { put(&copy, "config.txt", b"player setting"); }
            let report = profile::deactivate_report(&d.dirs, &d.g).unwrap();
            if edited {
                assert!(report.retained.iter().any(|r| r.path == copy && r.may_run), "{which}: {report:?}");
                assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"player setting");
                let again = kiln_manage::engine_mods::unplace(&d.g.path).unwrap();
                assert!(again.retained.iter().any(|r| r.path == copy));
            } else {
                assert!(report.removed.contains(&copy), "{which}: {report:?}");
                assert!(!copy.exists());
            }
        }
    }
}

#[test]
fn native_game_settings_never_discard_earlier_stashed_values_or_unrestored_files() {
    for (which, kind, folder) in [("alp-full", ModKind::Eml, "auto-loot-plus"),
                                  ("alp-lite", ModKind::Native, "auto-loot-plus"),
                                  ("xhl-plugin", ModKind::Xhl, "XHL-Vein-Mining")] {
        let d = drop_on_launcher(which, "Stash package");
        let mut p = my_mods(&d);
        let id = p.mods.list(kind)[0].id.clone();
        let stash = d.g.path.join("kiln/state/placed-settings").join(kind.as_str()).join(&id);
        let stash = if kind == ModKind::Native { stash.join("auto-loot-plus") } else { stash };
        put(&stash, "config.txt", b"earlier player setting");
        put(&stash, "notes.md", b"player recovery notes");
        let copy = d.g.path.join("mods").join(folder);
        put(&copy, "config.txt", b"latest player setting");
        p.mods.list_mut(kind)[0].enabled = false;
        p = activate(&d, &p);
        assert!(!copy.exists());
        assert_eq!(std::fs::read(stash.join("config.txt")).unwrap(), b"latest player setting");
        assert_eq!(std::fs::read(stash.join("config.txt.kept-1")).unwrap(), b"earlier player setting");
        assert_eq!(std::fs::read(stash.join("notes.md")).unwrap(), b"player recovery notes");
        p.mods.list_mut(kind)[0].enabled = true;
        activate(&d, &p);
        assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"latest player setting");
        assert!(!stash.join("config.txt").exists(), "only the restored value leaves the stash");
        assert_eq!(std::fs::read(stash.join("config.txt.kept-1")).unwrap(), b"earlier player setting");
        assert_eq!(std::fs::read(stash.join("notes.md")).unwrap(), b"player recovery notes");
    }
}

#[test]
fn xhl_cleanup_requires_exact_receipts_and_retains_player_changes() {
    for changed in [None, Some("config.txt"), Some("runtime.json"), Some("XHL-Vein-Mining.dll"), Some("native-plugin.ini")] {
        let d = drop_on_launcher("xhl-plugin", "XHL cleanup");
        let copy = d.g.path.join("mods/XHL-Vein-Mining");
        assert_eq!(my_mods(&d).mods.xhl[0].id, "xhl-vein-mining");
        if let Some(file) = changed { put(&copy, file, b"player bytes"); }
        let report = profile::deactivate_report(&d.dirs, &d.g).unwrap();
        if let Some(file) = changed {
            let kept = report.retained.iter().find(|r| r.path == copy).expect("changed XHL folder retained");
            assert!(kept.may_run && !kept.reason.is_empty(), "{report:?}");
            assert_eq!(std::fs::read(copy.join(file)).unwrap(), b"player bytes");
            assert!(report.warning().unwrap().contains("XHL-Vein-Mining"));
            let again = kiln_manage::engine_mods::unplace(&d.g.path).unwrap();
            assert!(again.retained.iter().any(|r| r.path == copy && r.may_run));
        } else {
            assert!(report.removed.contains(&copy), "{report:?}");
            assert!(!copy.exists());
        }
    }
}

#[test]
fn xhl_cleanup_never_adopts_markers_or_removes_nested_or_unflagged_copies() {
    for scenario in ["marker-only", "nested", "unflagged"] {
        let d = drop_on_launcher("xhl-plugin", "XHL ownership");
        let mut copy = d.g.path.join("mods/XHL-Vein-Mining");
        let file = d.g.path.join("kiln/state/library-placements.json");
        let mut receipts: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        match scenario {
            "marker-only" => receipts = serde_json::json!([]),
            "nested" => {
                let nested = d.g.path.join("mods/nested/XHL-Vein-Mining");
                std::fs::create_dir_all(nested.parent().unwrap()).unwrap();
                std::fs::rename(&copy, &nested).unwrap();
                copy = nested;
                receipts[0]["path"] = serde_json::json!(copy);
            }
            _ => receipts[0]["game_folder"] = serde_json::json!(false),
        }
        std::fs::write(&file, serde_json::to_vec(&receipts).unwrap()).unwrap();
        let report = kiln_manage::engine_mods::unplace(&d.g.path).unwrap();
        assert!(!report.removed.contains(&copy), "{scenario}: {report:?}");
        assert_eq!(std::fs::read(copy.join("XHL-Vein-Mining.dll")).unwrap(), xhl_plugin());
        if scenario != "marker-only" {
            assert!(report.retained.iter().any(|r| r.path == copy && r.may_run), "{scenario}: {report:?}");
        }
    }
}

#[test]
fn copy_mode_uses_the_native_mods_copy_too() {
    let d = drop_on_launcher("alp-full", "AutoLootPlus 1.1.8");
    let p = my_mods(&d);
    let rendered = profile::render(&d.dirs, &p, &d.g, PathMode::Copy).unwrap();
    assert_eq!(rendered.mods.eml[0].path.replace('\\', "/"), "../mods/auto-loot-plus");
    if let Some(out) = std::env::var_os("KILN_PROFILE_FIXTURE_OUT") {
        std::fs::write(out, serde_json::to_vec_pretty(&rendered).unwrap()).unwrap();
    }
}

fn assert_xhl_contract(dirs: &DataDirs, g: &GameInstall, native_count: usize) {
    let p = profile::get(dirs, "my-mods").unwrap();
    let f = profile::read_game_profile(&g.path).unwrap().unwrap();
    assert_eq!(p.mods.xhl.len(), 1);
    assert!(!p.dropins.contains(&ModKind::Xhl));
    let found = kiln_manage::found::scan(dirs, g, &p);
    assert!(!found.mods.iter().any(|m| m.kind == Some(ModKind::Xhl)), "receipt-owned copies are not manual drop-ins: {found:?}");
    assert_eq!(f.mods.xhl.len(), 1);
    assert!(f.mods.xhl[0].path.ends_with("mods\\XHL-Vein-Mining"));
    assert!(f.mods.eml.is_empty() && f.mods.plugin.is_empty() && f.mods.kiln.is_empty());
    assert_eq!(f.mods.native.len(), native_count);
    let preview = profile::resolve_preview(dirs, &p, kiln_manage::manifest::RunSide::Client).unwrap();
    assert_eq!(preview.native.len(), native_count);
    assert!(preview.native.iter().all(|n| !n.id.contains("vein")));
    let chain = kiln_manage::chain::read(&g.path).unwrap();
    assert!(chain.chain.iter().chain(&chain.profile).chain(&chain.late).all(|e| !e.path.contains("Vein")));
    let l = library::list(dirs, Some(ModKind::Xhl)).unwrap();
    let src = &l[0].versions[0].folder;
    for name in ["mod.json", "native.json", "src/mod.lua"] { assert!(!src.join(name).exists()); }
    for name in ["native-plugin.ini", "XHL-Vein-Mining.dll", "config.txt"] {
        assert_eq!(std::fs::read(src.join(name)).unwrap(), std::fs::read(g.path.join("mods/XHL-Vein-Mining").join(name)).unwrap());
    }
    assert_eq!(profile::list(dirs).unwrap()[0].counts.xhl, 1);
    assert_eq!(profile::render(dirs, &p, g, PathMode::Copy).unwrap().mods.xhl[0].path, "../mods/XHL-Vein-Mining");
    assert!(!ModKind::CORE.contains(&ModKind::Xhl));
    assert!(ModKind::Xhl.engine().is_none() && kiln_manage::engine_mods::plugin_of(ModKind::Xhl).is_none());
    // The native core's fixed ABI and validated engine arrays stay unchanged: XHL has no execution route there.
    let header = include_str!("../../../native/core/src/profile.h");
    assert!(!header.contains("\"xhl\""));
}

#[test]
fn pure_xhl_combined_and_separate_imports_only_execute_the_host() {
    for (combined, server) in [(false, false), (true, false), (true, true)] {
        let t = tmp("xhl-host");
        let dirs = DataDirs::at(t.0.join("data"));
        let mut g = game_with_z(&t.0);
        if server {
            g.kind = GameKind::Server;
            g.exe = "enshrouded_server.exe".into();
            put(&g.path, "enshrouded_server.exe", b"MZ");
        }
        let pkg = t.0.join("XHL bundle");
        layout(&pkg, "xhl-plugin");
        put(&pkg, "mods/XHL-Vein-Mining/data/table.bin", b"unchanged data\0\xff");
        if combined { layout(&pkg, "xhl-core"); }
        let archive = t.0.join("XHL bundle.zip");
        zip_of(&pkg, &archive);
        if server {
            // Quick-add is client-only. Dedicated profiles use the same Library importer and receipt placement.
            let r = library::import(&dirs, &[archive], &mut NoProgress).unwrap();
            assert!(r.skipped.is_empty());
            let mut p = profile::create(&dirs, "My Mods", None).unwrap();
            for r in r.imported {
                p.mods.list_mut(r.kind).push(profile::ProfileMod { id: r.id, version: r.version, hash: r.hash, enabled: true });
            }
            profile::save(&dirs, &p).unwrap();
            profile::activate(&dirs, &p.id, &g).unwrap();
        } else {
            kiln_manage::quick::add_with(&dirs, &g, &[archive], false, &mut NoProgress).unwrap();
        }
        assert_xhl_contract(&dirs, &g, usize::from(combined));
        let status = kiln_manage::engine_mods::status(&g.path).into_iter().find(|s| s.kind == ModKind::Xhl).unwrap();
        assert_eq!(status.prerequisite.unwrap().available, combined);
        assert!(status.state.is_none() && status.plugin.is_empty());
        if !combined {
            let core = t.0.join("XHL-Core");
            layout(&core, "xhl-core");
            kiln_manage::quick::add_with(&dirs, &g, &[core], false, &mut NoProgress).unwrap();
            assert_xhl_contract(&dirs, &g, 1);
            assert!(kiln_manage::engine_mods::xhl_host(&g.path).available);
            let mut p = profile::get(&dirs, "my-mods").unwrap();
            p.mods.native[0].enabled = false;
            profile::save(&dirs, &p).unwrap();
            profile::activate(&dirs, &p.id, &g).unwrap();
            assert!(!kiln_manage::engine_mods::xhl_host(&g.path).available);
            assert!(g.path.join("mods/XHL-Vein-Mining/XHL-Vein-Mining.dll").is_file());
        }
        assert_eq!(std::fs::read(g.path.join("mods/XHL-Vein-Mining/data/table.bin")).unwrap(), b"unchanged data\0\xff");
    }
}

#[test]
fn pure_xhl_updates_switches_and_removed_selections_preserve_player_settings() {
    let d = drop_on_launcher("xhl-plugin", "XHL plugin");
    let copy = d.g.path.join("mods/XHL-Vein-Mining");
    put(&copy, "config.txt", b"player setting");
    let update = d._t.0.join("update");
    layout(&update, "xhl-plugin");
    put(&update, "mods/XHL-Vein-Mining/config.txt", b"new default");
    put(&update, "mods/XHL-Vein-Mining/new-data.bin", b"new data");
    kiln_manage::quick::add_with(&d.dirs, &d.g, &[update], false, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"player setting");
    assert_eq!(std::fs::read(copy.join("new-data.bin")).unwrap(), b"new data");
    let mut p = my_mods(&d);
    let selection = p.mods.xhl[0].clone();
    p.mods.xhl[0].enabled = false;
    p = profile::save(&d.dirs, &p).unwrap();
    assert!(profile::write_once(&d.dirs, &p.id, &d.g).is_err());
    profile::activate(&d.dirs, &p.id, &d.g).unwrap();
    assert!(!copy.exists());
    p.mods.xhl[0].enabled = true;
    p = activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"player setting");
    p.mods.xhl.clear();
    p = activate(&d, &p);
    assert!(!copy.exists());
    p.mods.xhl.push(selection);
    activate(&d, &p);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"player setting");
}

#[test]
fn pure_xhl_manual_conflicts_and_edited_plugin_code_are_never_overwritten() {
    for changed in ["manual", "XHL-Vein-Mining.dll", "native-plugin.ini"] {
        let d = drop_on_launcher("xhl-plugin", "XHL plugin");
        let copy = d.g.path.join("mods/XHL-Vein-Mining");
        let mut p = my_mods(&d);
        if changed == "manual" {
            p.mods.xhl.clear(); p = activate(&d, &p);
            put(&copy, "native-plugin.ini", b"manual INI");
            let r = &d.report.imported[0];
            p.mods.xhl.push(profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true });
            activate(&d, &p);
            assert_eq!(std::fs::read(copy.join("native-plugin.ini")).unwrap(), b"manual INI");
            assert!(!copy.join(".kiln-library.json").exists());
        } else {
            put(&copy, changed, b"player edited code");
            p.mods.xhl.clear(); activate(&d, &p);
            assert_eq!(std::fs::read(copy.join(changed)).unwrap(), b"player edited code");
            let rep = kiln_manage::engine_mods::place(&d.dirs, &my_mods(&d), &d.g.path).unwrap();
            assert_eq!(rep.changed, ["xhl:xhl-vein-mining"]);
        }
    }
}

#[test]
fn pure_xhl_direct_wrapped_and_archive_root_names_do_not_use_temporary_extraction_names() {
    for shape in ["direct", "wrapper", "archive-root"] {
        let t = tmp("xhl-layout");
        let dirs = DataDirs::at(t.0.join("data"));
        let g = game_with_z(&t.0);
        let outer = t.0.join("wrapper");
        let folder = outer.join("XHL-Vein-Mining");
        put(&folder, "native-plugin.ini", b"[Plugin]\nDll=XHL-Vein-Mining.dll\n");
        put(&folder, "XHL-Vein-Mining.dll", &xhl_plugin());
        put(&folder, "config.txt", b"x");
        let src = match shape {
            "direct" => folder,
            "wrapper" => { let z = t.0.join("wrapped.zip"); zip_of(&outer, &z); z },
            _ => { let z = t.0.join("XHL-Vein-Mining.zip"); zip_of(&folder, &z); z },
        };
        kiln_manage::quick::add_with(&dirs, &g, &[src], false, &mut NoProgress).unwrap();
        assert_xhl_contract(&dirs, &g, 0);
    }
}

#[test]
fn an_unrelated_chain_loader_never_satisfies_the_xhl_host_requirement() {
    let d = drop_on_launcher("alp-lite", "AutoLootPlus Lite");
    assert!(!kiln_manage::engine_mods::xhl_host(&d.g.path).available);
    put(&d.g.path, "dbghelp.dll", &eml_dbghelp());
    assert!(!kiln_manage::engine_mods::xhl_host(&d.g.path).available);
    put(&d.g.path, "dinput8.dll", &xhl_core());
    assert!(kiln_manage::engine_mods::xhl_host(&d.g.path).available);
    std::fs::remove_file(d.g.path.join("dinput8.dll")).unwrap();
    assert!(!kiln_manage::engine_mods::xhl_host(&d.g.path).available);
}
