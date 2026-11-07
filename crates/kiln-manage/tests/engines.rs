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
fn eml_capabilities_and_legacy_extensions_do_not_imply_shroudforge() {
    let t = tmp("eml-real");
    for (tag, ext, kind) in [
        ("plain", None, DetectKind::Eml),
        ("legacy", Some(r#"{"launcher":"EML"}"#), DetectKind::Eml),
        ("sf", Some(r#"{"launcher":"SF"}"#), DetectKind::ShroudForge),
    ] {
        let d = t.0.join(tag);
        put(&d, "mod.json", br#"{"id":"stacks","name":"Stacks","version":"1.0.0","capabilities":["patch","runtime"]}"#);
        put(&d, "src/mod.lua", b"return {}");
        if let Some(ext) = ext { put(&d, "extended.mod.json", ext.as_bytes()); }
        let found = detect(&d);
        assert_eq!(found.kind, kind, "{found:?}");
        assert!(found.reason.contains(if kind == DetectKind::Eml { "EML" } else { "ShroudForge" }));
        assert!(found.reason.contains("Library importer"), "engine_mods imports it: {found:?}");
        assert!(found.kind.importable());
    }
}

#[test]
fn odd_manifests_never_fall_through_to_helper_dll_imports() {
    let t = tmp("odd-manifest");
    for (tag, manifest) in [("broken", b"{".as_slice()), ("unknown", br#"{"id":"odd"}"#.as_slice()),
                            ("caps-only", br#"{"id":"odd","name":"Odd","version":"1.0.0","capabilities":["patch"]}"#.as_slice()),
                            ("lua-emf", br#"{"id":"lua","emf":26,"main":"main.lua"}"#.as_slice())] {
        let d = t.0.join(tag);
        put(&d, "mod.json", manifest);
        put(&d, "helper.dll", &build_test_dll(&["helper"], b""));
        put(&d, "main.lua", b"return {}");
        let found = detect(&d);
        assert_eq!(found.kind, DetectKind::Unknown, "{found:?}");
        assert!(found.items.is_empty(), "unknown package must stay intact: {found:?}");
        assert!(!found.reason.is_empty());
        let report = library::import(&DataDirs::at(t.0.join("data")), &[d], &mut NoProgress).unwrap();
        assert!(report.imported.is_empty() && report.existing.is_empty(), "{report:?}");
        assert_eq!(report.skipped.len(), 1);
    }
    let d = t.0.join("no-manifest");
    put(&d, "src/mod.lua", b"return {}");
    put(&d, "helper.dll", &build_test_dll(&["helper"], b""));
    let found = detect(&d);
    assert_eq!(found.kind, DetectKind::Unknown);
    assert!(found.items.is_empty());
}

#[test]
fn sf_install_only_reports_its_own_helper_proxies() {
    let t = tmp("sf-proxy-owners");
    put(&t.0, kiln_manage::shroudforge::RUNTIME_DLL, b"runtime");
    put(&t.0, "dbghelp.dll", &build_test_dll(&[], b"EML_LOG_FILE_PATH"));
    put(&t.0, "winmm.dll", &build_test_dll(&[], b"shroudtopia"));
    let sf = kiln_manage::shroudforge::detect(&t.0).unwrap();
    assert!(!sf.dbghelp && !sf.bootstrap);
    for name in ["dbghelp.dll", "winmm.dll"] { put(&t.0, name, &build_test_dll(&["ShroudforgeSetDinput8Provider"], b"")); }
    let sf = kiln_manage::shroudforge::detect(&t.0).unwrap();
    assert!(sf.dbghelp && sf.bootstrap);
}

#[test]
fn own_proxy_without_dinput_export_and_x86_engine_dll_are_not_mods() {
    let t = tmp("dll-identity");
    let own = put(&t.0, "own.dll", &build_test_dll(&["KilnProxyInfo"], b""));
    assert_eq!(detect(&own).kind, DetectKind::Unknown);
    let mut x86 = build_test_dll(&["GreeneyeModStart"], b"");
    x86[0x84..0x86].copy_from_slice(&0x14cu16.to_le_bytes());
    put(&t.0.join("x86"), "mod.dll", &x86);
    assert_eq!(detect(&t.0.join("x86")).kind, DetectKind::Unknown);
}

#[test]
fn malformed_pe_exports_are_bounded_by_file_backed_sections() {
    use kiln_manage::pe::{self, ProxyKind};
    let good = build_test_dll(&["emf_loader_identity"], b"");
    let section = 0x188;
    let mut cases = Vec::new();
    let mut b = good.clone();
    b[section + 20..section + 24].copy_from_slice(&0xfffffff0u32.to_le_bytes());
    cases.push(b);
    let mut b = good.clone();
    b[section + 16..section + 20].copy_from_slice(&40u32.to_le_bytes()); // names live outside the raw section
    cases.push(b);
    let mut b = good.clone();
    b[0x94..0x96].copy_from_slice(&2u16.to_le_bytes()); // optional header no longer covers directories
    cases.push(b);
    let mut b = good.clone();
    b.truncate(0x190); // truncated section table
    cases.push(b);
    let mut b = good;
    b[0x218..0x21c].copy_from_slice(&0xffffffffu32.to_le_bytes()); // unbounded name count
    cases.push(b);
    for b in cases {
        assert!(pe::parse(&b).is_err());
        assert_eq!(pe::classify_bytes(&b).kind, ProxyKind::NotDll);
    }
    let wide: Vec<u8> = "emf-loader-7D3C9F".encode_utf16().flat_map(u16::to_le_bytes).collect();
    assert_eq!(pe::classify_bytes(&build_test_dll(&["ShroudforgeSetDinput8Provider"], &wide)).kind, ProxyKind::Emf);
}

#[cfg(unix)]
#[test]
fn odd_folder_links_and_deep_trees_do_not_recurse_or_import_dll_targets() {
    let t = tmp("links-depth");
    let outside = put(&t.0, "outside.dll", &build_test_dll(&["x"], b""));
    let folder = t.0.join("folder");
    std::fs::create_dir_all(&folder).unwrap();
    std::os::unix::fs::symlink(&folder, folder.join("loop")).unwrap();
    std::os::unix::fs::symlink(&outside, folder.join("helper.dll")).unwrap();
    assert_eq!(detect(&folder).kind, DetectKind::Unknown);
    assert_eq!(detect(&folder.join("helper.dll")).kind, DetectKind::Unknown);
    let report = library::import(&DataDirs::at(t.0.join("data")), &[folder], &mut NoProgress).unwrap();
    assert!(report.imported.is_empty());
    let root = t.0.join("deep");
    let mut d = root.clone();
    for _ in 0..40 { d.push("level"); }
    put(&d, "helper.dll", &build_test_dll(&[], b""));
    let found = detect(&root);
    assert_eq!(found.kind, DetectKind::Unknown);
    assert!(found.items.is_empty());
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
        }, DetectKind::Flame, Some(Engine::Flame), "Creative Mode: a Flame mod (Library importer: kiln-flame)"),
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
        }, DetectKind::Enshwick, Some(Engine::Enshwick), "Kiln can't import Enshwick mods into the Library yet; no importer is registered"),
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
         "not a mod to import"),
        (put(r, "emf/dinput8.dll", &build_test_dll(&["DirectInput8Create", "ShroudforgeSetDinput8Provider", "emf_loader_identity"], b"")),
         DetectKind::Proxy, Some(Engine::Emf), "EMF's own loader"),
        (put(r, "sf/dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"shroudforge/shroudforge-runtime.dll")), DetectKind::Proxy,
         Some(Engine::ShroudForge), "switch from ShroudForge"),
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
        // importable: the engine kinds Kiln runs (engine_mods); never the reserved kinds, loaders or files
        let runs = kiln_manage::manifest::ModKind::of_detect(d.kind).is_some_and(|k| kiln_manage::engine_mods::IMPORTED.contains(&k));
        assert_eq!(d.kind.importable(), runs, "{}: {:?}", path.display(), d.kind);
    }
    // Kiln's own kinds are untouched: a stand-alone dinput8 mod and a plain DLL stay bare DLLs
    assert_eq!(detect(&put(r, "s/dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"sprint toggle"))).kind, DetectKind::BareDll);
    assert_eq!(detect(&put(r, "s/fps.dll", &build_test_dll(&["x"], b"mentions reshade"))).kind, DetectKind::BareDll);
}

/// Creative Mode's zip as it is laid out: the Flame loader as dinput8.dll and mods/creative/. Before, both were imported
/// as plain DLLs that never started; now the mod goes into the library as a Flame mod (kiln-flame runs it) and the
/// loader is skipped with its reason.
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
    assert!(d.reason.contains("1 importable item(s) and 1 Kiln can't load"), "{}", d.reason);
    let dirs = DataDirs::at(t.0.join("data"));
    let rep = library::import(&dirs, std::slice::from_ref(&src), &mut NoProgress).unwrap();
    assert_eq!(rep.imported.iter().map(|r| (r.kind, r.id.as_str())).collect::<Vec<_>>(), [(kiln_manage::manifest::ModKind::Flame, "creative")], "{rep:?}");
    assert_eq!(rep.skipped.len(), 1);
    assert!(rep.skipped[0].reason.contains("not a mod to import"), "{:?}", rep.skipped);
    let l = library::list(&dirs, None).unwrap();
    assert_eq!(l.len(), 1);
    assert!(l[0].versions[0].folder.join("creative.dll").is_file() && l[0].versions[0].folder.join("flame.json").is_file());
}

