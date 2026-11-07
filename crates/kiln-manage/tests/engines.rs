//! Library detection of mods made for other engines (engines.rs): every kind, the reason a player reads, and the
//! rule that an unsupported kind is never imported as a plain DLL. Temp folders and fake DLLs only.

use kiln_manage::engines::Engine;
use kiln_manage::library::{self, DetectKind};
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::task::NoProgress;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-engines-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}
fn put(root: &Path, rel: &str, data: &[u8]) -> PathBuf {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(&p, data).unwrap();
    p
}

fn detect(p: &Path) -> library::Detected {
    library::detect(p).unwrap()
}

#[test]
fn every_engine_kind_is_detected_with_its_reason() {
    let t = tmp("kinds");
    let r = &t.0;
    let cases: Vec<(PathBuf, DetectKind, Option<Engine>, &str)> = vec![
        // Flame (Creative Mode): flame.json in the mod folder
        ({
            let d = r.join("creative");
            put(&d, "mod.json", br#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#);
            put(&d, "flame.json", br#"{"dll":"creative.dll"}"#);
            put(&d, "creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
            put(&d, "sigs/26430625.json", b"{}");
            d
        }, DetectKind::Flame, Some(Engine::Flame), "Creative Mode: a Flame mod. Kiln can't run Flame mods yet; support is coming"),
        // ShroudForge package: EML layout + extended.mod.json
        ({
            let d = r.join("sfpkg");
            put(&d, "mod.json", br#"{"id":"sfpkg","name":"SF Pack","version":"1.0.0"}"#);
            put(&d, "extended.mod.json", br#"{"enabled":true}"#);
            put(&d, "src/mod.lua", b"-- lua");
            d
        }, DetectKind::ShroudForge, Some(Engine::ShroudForge), "ShroudForge"),
        // EML mod: mod.json + src/mod.lua, nothing of ShroudForge's
        ({
            let d = r.join("emlmod");
            put(&d, "mod.json", br#"{"id":"emlmod","name":"Bigger Stacks","version":"0.1.0"}"#);
            put(&d, "src/mod.lua", b"-- lua");
            d
        }, DetectKind::Eml, Some(Engine::Eml), "Bigger Stacks"),
        // Enshwick data pack
        ({
            let d = r.join("author.pack");
            put(&d, "pack.toml", b"id = \"author.pack\"\n");
            d
        }, DetectKind::Enshwick, Some(Engine::Enshwick), "Enshwick"),
        // Enshwick DLL mod (with a helper DLL of its own: the folder is the mod)
        ({
            let d = r.join("author.lockers");
            put(&d, "lockers.dll", &build_test_dll(&["enshwick_mod_abi", "enshwick_mod_query", "enshwick_mod_init"], b""));
            put(&d, "helper.dll", &build_test_dll(&["helper"], b""));
            d
        }, DetectKind::Enshwick, Some(Engine::Enshwick), "Kiln can't run Enshwick mods yet; support is coming"),
        // SET mod
        ({
            let d = r.join("set-mod");
            put(&d, "init.lua", b"print('hi')");
            put(&d, "mod.json", br#"{"name":"Fly Mode"}"#);
            d
        }, DetectKind::Set, Some(Engine::Set), "Fly Mode: a Shrouded Engine Tweaks mod"),
        // Greeneye mod
        ({
            let d = r.join("worstmap");
            put(&d, "mod.json", br#"{"id":"worstmap","name":"The Worst Map Mod","version":"1.0.0","dll":"map.dll","side":"client","api":2}"#);
            put(&d, "map.dll", &build_test_dll(&["GreeneyeModStart"], b""));
            d
        }, DetectKind::Greeneye, Some(Engine::Greeneye), "The Worst Map Mod: a Greeneye Loader mod"),
        // Shroudtopia mod DLL
        (put(r, "loose/minimap.dll", &build_test_dll(&["CreateModInstance"], b"")), DetectKind::Shroudtopia, Some(Engine::Shroudtopia),
         "minimap.dll: a Shroudtopia mod"),
        // loaders under proxy names
        (put(r, "eml/dbghelp.dll", &build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH")), DetectKind::Proxy, Some(Engine::Eml),
         "dbghelp.dll is EML (Enshrouded Mod Loader)'s loader"),
        (put(r, "gl/dbgcore.dll", &build_test_dll(&["MiniDumpWriteDump"], b"Greeneye Loader")), DetectKind::Proxy, Some(Engine::Greeneye),
         "support is coming"),
        (put(r, "emf/dinput8.dll", &build_test_dll(&["DirectInput8Create", "ShroudforgeSetDinput8Provider", "emf_loader_identity"], b"")),
         DetectKind::Proxy, Some(Engine::Emf), "EMF's own loader"),
        (put(r, "sf/dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"shroudforge/shroudforge-runtime.dll")), DetectKind::Proxy,
         Some(Engine::ShroudForge), "can't run next to Kiln"),
        (put(r, "lm/iphlpapi.dll", &build_test_dll(&["GetAdaptersInfo"], b"livemap.ini")), DetectKind::Proxy, Some(Engine::LiveMap),
         "copy it into the server folder"),
        // files that are no mods
        (put(r, "f/3ad85aea_info", b"x"), DetectKind::Save, None, "a save file"),
        (put(r, "f/world.ksd", b"x"), DetectKind::Save, None, "a save file"),
        (put(r, "f/Enshrouded.CT", b"x"), DetectKind::CheatTable, None, "Cheat Engine table"),
        (put(r, "f/MyReShade.ini", b"x"), DetectKind::Reshade, Some(Engine::ReShade), "ReShade preset"),
        (put(r, "f/data_0001.kfc", b"x"), DetectKind::Data, None, "patched game data file"),
        (put(r, "f/plugin.asi", b"x"), DetectKind::Asi, Some(Engine::UltimateAsiLoader), "Ultimate ASI Loader"),
    ];
    for (path, kind, engine, words) in cases {
        let d = detect(&path);
        assert_eq!((d.kind, d.engine), (kind, engine), "{}: {}", path.display(), d.reason);
        assert!(d.reason.contains(words), "{}: {:?} lacks {words:?}", path.display(), d.reason);
        assert!(!d.reason.ends_with('.'), "{}: the launcher adds the period: {:?}", path.display(), d.reason);
        assert!(!d.kind.importable(), "{}: no loader is registered for {:?}", path.display(), d.kind);
    }
    // Kiln's own kinds are untouched: a stand-alone dinput8 mod and a plain DLL stay bare DLLs
    assert_eq!(detect(&put(r, "s/dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"sprint toggle"))).kind, DetectKind::BareDll);
    assert_eq!(detect(&put(r, "s/fps.dll", &build_test_dll(&["x"], b"mentions reshade"))).kind, DetectKind::BareDll);
}

/// Creative Mode's zip as it is laid out: the Flame loader as dinput8.dll and mods/creative/. Before, both were imported
/// as plain DLLs that never started; now neither is imported and the player reads why.
#[cfg(feature = "zip")]
#[test]
fn creative_mode_is_never_imported_as_plain_dlls() {
    let t = tmp("creative");
    let src = t.0.join("CreativeMode");
    put(&src, "dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"FlameStartRuntime\0mods\\\0flame.json"));
    put(&src, "mods/creative/mod.json", br#"{"id":"creative","name":"Creative Mode","version":"1.4.6"}"#);
    put(&src, "mods/creative/flame.json", br#"{"dll":"creative.dll"}"#);
    put(&src, "mods/creative/creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    put(&src, "mods/creative/symbols/a.json", b"{}");
    let d = detect(&src);
    assert_eq!(d.kind, DetectKind::Unknown);
    let kinds: Vec<(DetectKind, Option<Engine>)> = d.items.iter().map(|i| (i.kind, i.engine)).collect();
    assert_eq!(kinds, [(DetectKind::Proxy, Some(Engine::Flame)), (DetectKind::Flame, Some(Engine::Flame))]);
    assert!(d.reason.contains("Flame"), "{}", d.reason);
    let dirs = DataDirs::at(t.0.join("data"));
    let rep = library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap();
    assert!(rep.imported.is_empty() && rep.existing.is_empty(), "{rep:?}");
    assert_eq!(rep.skipped.len(), 2);
    assert!(rep.skipped.iter().all(|s| s.reason.contains("support is coming")), "{:?}", rep.skipped);
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

