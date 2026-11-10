//! Real Nexus packages (the path lists in the dev.22 survey, built by hand with tiny files): what the Library imports
//! from a zip, a folder, the game's `mods` folder or one mod folder, what it leaves out and what it says about it.
#![cfg(all(unix, feature = "zip"))]

pub mod shapes {
    use kiln_manage::library::{self, ImportReport};
    use kiln_manage::manifest::ModKind;
    use kiln_manage::paths::DataDirs;
    use kiln_manage::pe::build_test_dll;
    use kiln_manage::task::NoProgress;
    use std::io::Write;
    use std::path::{Path, PathBuf};

    pub struct Tmp(pub PathBuf);
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    pub fn tmp(tag: &str) -> Tmp {
        static N: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
        let n = N.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let p = std::env::temp_dir().join(format!("km-import-shapes-{tag}-{}-{n}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        Tmp(p.canonicalize().unwrap())
    }
    pub fn put(root: &Path, rel: &str, data: &[u8]) {
        let p = root.join(rel);
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(p, data).unwrap();
    }

    // The DLLs the survey's packages carry, as the markers of engines.rs know them.
    pub fn eml_dbghelp() -> Vec<u8> { build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH") }
    pub fn eml_dinput8() -> Vec<u8> { build_test_dll(&["DirectInput8Create"], b"EML_LOG_FILE_PATH") }
    pub fn st_winmm() -> Vec<u8> { build_test_dll(&["timeGetTime"], b"shroudtopia.dll") }
    pub fn st_loader() -> Vec<u8> { build_test_dll(&["x"], b"shroudtopia loader") }
    pub fn st_mod() -> Vec<u8> { build_test_dll(&["CreateModInstance"], b"") }
    pub fn plain_dinput8() -> Vec<u8> { build_test_dll(&["DirectInput8Create"], b"a mod's own proxy") }
    pub fn plain_winmm() -> Vec<u8> { build_test_dll(&["timeGetTime"], b"a mod's own proxy") }
    pub fn plain_powrprof() -> Vec<u8> { build_test_dll(&["PowerGetActiveScheme"], b"a mod's own proxy") }

    pub fn eml_json(id: &str, name: &str) -> Vec<u8> {
        format!(r#"{{"id":"{id}","name":"{name}","version":"1.0.0","capabilities":["runtime"]}}"#).into_bytes()
    }

    /// One EML mod folder as authors ship it: mod.json, src/mod.lua and a readme.
    pub fn eml_mod(root: &Path, dir: &str, id: &str, name: &str) {
        put(root, &format!("{dir}/mod.json"), &eml_json(id, name));
        put(root, &format!("{dir}/src/mod.lua"), b"return {}");
        put(root, &format!("{dir}/README.md"), b"read me");
    }

    /// The layouts of nexus-layouts.md, by Nexus mod number, written below `root`.
    pub fn layout(root: &Path, which: &str) {
        match which {
            "136-eml-included" => {
                put(root, "dbghelp.dll", &eml_dbghelp());
                put(root, "dinput8.dll", &eml_dinput8());
                eml_mod(root, "mods/HoardersHelper", "HoardersHelper", "Hoarder's Helper");
                put(root, "mods/HoardersHelper/src/HoardersHelperConfig.lua", b"return {}");
            }
            "136-mod-only" => {
                eml_mod(root, "mods/HoardersHelper", "HoardersHelper", "Hoarder's Helper");
            }
            "127-xhl" => {
                put(root, "dbghelp.dll", &eml_dbghelp());
                eml_mod(root, "mods/XHL-Max-Stack-65535", "XHL-Max-Stack-65535", "XHL Max Stack 65535");
                put(root, "mods/XHL-Max-Stack-65535/config.txt", b"65535");
            }
            "79-ember" => eml_mod(root, "Ember", "Ember", "Ember"),
            "72-fog" => eml_mod(root, "Less Dense Shroud Fog", "Less Dense Shroud Fog", "Less (or More) Dense Shroud Fog"),
            "68-antisocialian" => eml_mod(root, "antisocialian's tweaks", "antisocialian's tweaks", "antisocialian's Tweaks"),
            "111-max-stack" => {
                eml_mod(root, "max-stack", "max-stack", "Max Stack");
                put(root, "max-stack/config.txt", b"x");
            }
            "67-no-intro" => eml_mod(root, "no_intro", "no_intro", "No Intro"),
            "164-map-zoom" => {
                put(root, "dbghelp.dll", &eml_dbghelp());
                put(root, "dinput8.dll", &eml_dinput8());
                put(root, "eml.json", br#"{"version":"0.2.0"}"#);
                put(root, "EML_LICENSE_GPL3.txt", b"GPL");
                put(root, "README.txt", b"readme");
                eml_mod(root, "mods/map_zoom", "map_zoom", "Map Zoom OUT");
            }
            "43-shroudtopia" => {
                put(root, "winmm.dll", &st_winmm());
                put(root, "shroudtopia.dll", &st_loader());
                put(root, "shroudtopia.json", br#"{"active":true,"mods":{}}"#);
                put(root, "mods/ChatCommands.dll", &st_mod());
                put(root, "mods/first_person_view.dll", &st_mod());
                put(root, "mods/whitelist_mod.dll", &st_mod());
                put(root, "mods/basics_mod/basics_mod.dll", &st_mod());
                put(root, "mods/basics_mod/mod.json", br#"{"id":"basics","name":"Basics","version":"1.0.0"}"#);
                put(root, "mods/flight_mod/flight_mod.dll", &st_mod());
                put(root, "mods/flight_mod/mod.json", br#"{"id":"flight","name":"Flight","version":"1.0.0"}"#);
            }
            "118-always-flying" => {
                put(root, "README.txt", b"readme");
                put(root, "CHANGELOG.txt", b"1.0.0");
                put(root, "PERMISSIONS.txt", b"free");
                put(root, "flight_mod/flight_mod.dll", &st_mod());
                put(root, "flight_mod/mod.json", br#"{"id":"flight","name":"Always Flying","version":"1.0.0"}"#);
            }
            // a proxy DLL with the files it reads
            "186-atlas" => {
                put(root, "dinput8.dll", &plain_dinput8());
                put(root, "Embervale Atlas - Licenses.txt", b"licences");
                put(root, "Embervale Atlas - Readme.txt", b"readme");
                put(root, "Uninstall Embervale Atlas.bat", b"del dinput8.dll");
                put(root, "embervale_atlas.dat", b"atlas data");
                put(root, "embervale_atlas_map.jpg", b"jpg");
            }
            "154-shroudhud-standalone" => {
                put(root, "README.txt", b"readme");
                put(root, "dinput8.dll", &plain_dinput8());
                put(root, "fomod/ModuleConfig.xml", b"<config/>");
                put(root, "vortex_override_instructions.json", b"{}");
            }
            "165-sprint-toggle" => {
                put(root, "LICENSE.txt", b"mit");
                put(root, "README.txt", b"readme");
                put(root, "dinput8.dll", &plain_dinput8());
                put(root, "ToggleSprint.ini", b"[Sprint]\nkey=shift\n");
            }
            "149-no-intro" => {
                put(root, "ReadMe.txt", b"readme");
                put(root, "winmm.dll", &plain_winmm());
            }
            "113-shroudscaler" => put(root, "winmm.dll", &plain_winmm()),
            "152-mod-menu" => {
                put(root, "POWRPROF.dll", &build_test_dll(&["PowerGetActiveScheme"], b"aldrich-modmenu"));
                put(root, "aldrich-modmenu/Cinzel.ttf", b"font");
                put(root, "aldrich-modmenu/aldrich-modmenu.ini", b"[menu]\n");
                put(root, "aldrich-modmenu/VK_LAYER_ALDRICH_modmenu.json", b"{}");
                put(root, "aldrich-modmenu/aldrich_modmenu_layer.dll", &build_test_dll(&["vkNegotiateLoaderLayerInterfaceVersion"], b""));
            }
            "141-dlss-fg" => {
                put(root, "powrprof.dll", &plain_powrprof());
                put(root, "EnshroudedVKFG.ini", b"[fg]\n");
                put(root, "Streamline/sl.interposer.dll", &build_test_dll(&["slInit"], b""));
                put(root, "Streamline/nvngx_dlssg.dll", &build_test_dll(&["x"], b""));
                put(root, "collect_logs_framegen.bat", b"echo");
                put(root, "uninstall_framegen.bat", b"del");
            }
            other => panic!("no layout {other}"),
        }
    }

    /// The same files as a zip (entry names as in the path list).
    pub fn zip_of(dir: &Path, out: &Path) {
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

    pub fn import(paths: &[PathBuf]) -> (Tmp, DataDirs, ImportReport) {
        let t = tmp("data");
        let dirs = DataDirs::at(t.0.join("data"));
        let r = library::import(&dirs, paths, &mut NoProgress).unwrap();
        (t, dirs, r)
    }

    /// Imports into an existing library.
    pub fn import_into(dirs: &DataDirs, paths: &[PathBuf]) -> ImportReport {
        library::import(dirs, paths, &mut NoProgress).unwrap()
    }

    /// `kind/id` of everything imported or already there, sorted.
    pub fn found(r: &ImportReport) -> Vec<String> {
        let mut v: Vec<String> = r.imported.iter().chain(&r.existing).map(|x| format!("{}/{}", x.kind.as_str(), x.id)).collect();
        v.sort();
        v
    }

    pub fn lib_ref(r: &ImportReport, kind: ModKind, id: &str) -> library::LibraryRef {
        r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned().unwrap_or_else(|| panic!("{kind:?}/{id}: {r:?}"))
    }

    pub fn summary(r: &ImportReport) -> String {
        let mut out = Vec::new();
        for x in r.imported.iter().chain(&r.existing) {
            out.push(format!("+ {}/{}", x.kind.as_str(), x.id));
        }
        for s in &r.skipped {
            out.push(format!("- {}: {}", Path::new(&s.path).file_name().unwrap().to_string_lossy(), s.reason));
        }
        for n in &r.notes {
            out.push(format!("i {}", n.text));
        }
        out.join("\n")
    }

    #[allow(dead_code)]
    pub fn kinds(r: &ImportReport) -> Vec<(ModKind, String)> {
        r.imported.iter().chain(&r.existing).map(|x| (x.kind, x.id.clone())).collect()
    }
}


use kiln_manage::library;
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::task::NoProgress;
use shapes::*;
use std::path::{Path, PathBuf};

const EML_LOADER_NOTE: &str = "EML's own loader is in this package: not needed, Kiln runs EML mods";

/// The same package three ways: its zip, the extracted folder, and (below) one mod folder out of it.
fn ways(which: &str) -> (Tmp, Vec<(&'static str, PathBuf)>) {
    let t = tmp(&format!("ways-{which}"));
    let d = t.0.join(which);
    layout(&d, which);
    let z = t.0.join(format!("{which}.zip"));
    zip_of(&d, &z);
    (t, vec![("zip", z), ("folder", d)])
}

#[test]
fn engine_mods_with_manifest_ids_that_are_not_kiln_ids_are_imported() {
    // (layout, library id, the manifest's own id, loader note expected)
    let cases = [
        ("136-eml-included", "hoardershelper", "HoardersHelper", true),
        ("136-mod-only", "hoardershelper", "HoardersHelper", false),
        ("127-xhl", "xhl-max-stack-65535", "XHL-Max-Stack-65535", true),
        ("79-ember", "ember", "Ember", false),
        ("72-fog", "less-dense-shroud-fog", "Less Dense Shroud Fog", false),
        ("68-antisocialian", "antisocialian-s-tweaks", "antisocialian's tweaks", false),
        ("164-map-zoom", "map_zoom", "map_zoom", true),
    ];
    for (which, id, manifest, note) in cases {
        let (_t, ways) = ways(which);
        for (how, path) in ways {
            let (_k, dirs, r) = import(&[path]);
            assert_eq!(found(&r), [format!("eml/{id}")], "{which} {how}: {r:?}");
            assert!(r.skipped.is_empty(), "{which} {how}: {:?}", r.skipped);
            assert_eq!(r.notes.iter().map(|n| n.text.as_str()).collect::<Vec<_>>(), if note { vec![EML_LOADER_NOTE] } else { vec![] }, "{which} {how}");
            let lr = lib_ref(&r, ModKind::Eml, id);
            let rec = library::record(&dirs, &lr).unwrap();
            // the manifest's own id is kept for the launcher and for the plugin's state rows; the files are the author's
            assert_eq!(rec.manifest_id.as_deref(), (manifest != id).then_some(manifest), "{which} {how}");
            let folder = library::path_of(&dirs, &lr).unwrap();
            let mj: serde_json::Value = serde_json::from_slice(&std::fs::read(folder.join("mod.json")).unwrap()).unwrap();
            assert_eq!(mj["id"], manifest, "{which} {how}: mod.json is not rewritten");
            assert!(folder.join("src/mod.lua").is_file());
            // names for the result the player reads
            assert!(r.names.get(&format!("eml/{id}")).is_some_and(|n| !n.is_empty()), "{:?}", r.names);
            let listed = library::list(&dirs, Some(ModKind::Eml)).unwrap();
            assert_eq!(listed[0].versions[0].manifest_id.as_deref(), (manifest != id).then_some(manifest));
        }
    }
}

#[test]
fn a_valid_id_is_its_own_and_records_no_manifest_id() {
    let (_t, ways) = ways("111-max-stack");
    let (_k, dirs, r) = import(&[ways[0].1.clone()]);
    assert_eq!(found(&r), ["eml/max-stack"]);
    assert_eq!(library::record(&dirs, &r.imported[0]).unwrap().manifest_id, None);
}

#[test]
fn one_mod_folder_and_the_games_mods_folder_import_like_the_package() {
    let t = tmp("mods-folder");
    let game = t.0.join("Enshrouded");
    layout(&game, "136-eml-included");
    put(&game, "mods/XHL-Max-Stack-65535/mod.json", &eml_json("XHL-Max-Stack-65535", "XHL"));
    put(&game, "mods/XHL-Max-Stack-65535/src/mod.lua", b"return {}");
    // a mods folder dropped as it is: every mod in it, no loader (it is not in that folder)
    let (_k, _d, r) = import(&[game.join("mods")]);
    assert_eq!(found(&r), ["eml/hoardershelper", "eml/xhl-max-stack-65535"], "{r:?}");
    assert!(r.skipped.is_empty() && r.notes.is_empty(), "{r:?}");
    // one mod folder out of it
    let (_k, _d, r) = import(&[game.join("mods/HoardersHelper")]);
    assert_eq!(found(&r), ["eml/hoardershelper"]);
    assert!(r.skipped.is_empty() && r.notes.is_empty());
    // the package's own folder with a wrapper level above it
    let wrapped = t.0.join("wrapped");
    layout(&wrapped.join("HoardersHelper EML included"), "136-eml-included");
    let (_k, _d, r) = import(&[wrapped]);
    assert_eq!(found(&r), ["eml/hoardershelper"]);
    assert_eq!(r.notes.len(), 1);
}

#[test]
fn a_package_that_is_only_a_loader_is_one_note_and_nothing_is_skipped() {
    let t = tmp("loader-only");
    let eml = t.0.join("eml");
    put(&eml, "dbghelp.dll", &eml_dbghelp());
    put(&eml, "dinput8.dll", &eml_dinput8());
    put(&eml, "eml.json", br#"{"version":"0.2.0"}"#);
    put(&eml, "EML_LICENSE_GPL3.txt", b"GPL");
    let (_k, _d, r) = import(&[eml]);
    assert!(r.imported.is_empty() && r.existing.is_empty() && r.skipped.is_empty(), "{r:?}");
    assert_eq!(r.notes.iter().map(|n| n.text.as_str()).collect::<Vec<_>>(), [EML_LOADER_NOTE], "both loader DLLs, one line");
}

#[test]
fn the_shroudtopia_loader_package_gives_each_mod_inside_it() {
    let (_t, ways) = ways("43-shroudtopia");
    for (how, path) in ways {
        let (_k, dirs, r) = import(&[path]);
        assert_eq!(found(&r), ["shroudtopia/basics", "shroudtopia/chatcommands", "shroudtopia/first_person_view", "shroudtopia/flight",
            "shroudtopia/whitelist_mod"], "{how}: {r:?}");
        assert!(r.skipped.is_empty(), "{how}: {:?}", r.skipped);
        // winmm.dll and shroudtopia.dll are the loader: said once
        assert_eq!(r.notes.iter().map(|n| n.text.as_str()).collect::<Vec<_>>(), ["Shroudtopia's own loader is in this package: not needed, Kiln runs Shroudtopia mods"]);
        // a loose DLL got the mod.json its plugin needs; a folder mod kept its own
        let chat = library::path_of(&dirs, &lib_ref(&r, ModKind::Shroudtopia, "chatcommands")).unwrap();
        assert!(chat.join("ChatCommands.dll").is_file() && chat.join("mod.json").is_file());
        let basics = library::path_of(&dirs, &lib_ref(&r, ModKind::Shroudtopia, "basics")).unwrap();
        assert!(basics.join("basics_mod.dll").is_file());
        assert!(!basics.join("winmm.dll").exists() && !basics.join("shroudtopia.dll").exists(), "the loader is never installed");
    }
}

#[test]
fn a_mod_folder_with_text_files_around_it_is_the_mod() {
    // #118: flight_mod/ next to README, CHANGELOG, PERMISSIONS
    let (_t, ways) = ways("118-always-flying");
    for (how, path) in ways {
        let (_k, _d, r) = import(&[path]);
        assert_eq!(found(&r), ["shroudtopia/flight"], "{how}: {r:?}");
        assert!(r.skipped.is_empty() && r.notes.is_empty(), "{how}");
    }
}

#[test]
fn two_manifest_ids_with_one_slug_never_share_a_library_entry() {
    let t = tmp("collide");
    let dirs = DataDirs::at(t.0.join("data"));
    let one = |name: &str, id: &str| {
        let d = t.0.join(name);
        eml_mod(&d, "m", id, id);
        d.join("m")
    };
    let a = import_into(&dirs, &[one("a", "Ember")]);
    let b = import_into(&dirs, &[one("b", "ember")]);
    let c = import_into(&dirs, &[one("c", "EMBER")]);
    let ids: Vec<String> = [&a, &b, &c].iter().map(|r| r.imported[0].id.clone()).collect();
    assert_eq!(ids[0], "ember");
    assert!(ids[1] != ids[0] && ids[2] != ids[0] && ids[1] != ids[2], "{ids:?}");
    assert!(ids.iter().all(|i| kiln_manage::manifest::is_mod_id(i)), "{ids:?}");
    // each remembers its own manifest id
    for (r, manifest) in [(&a, "Ember"), (&b, "ember"), (&c, "EMBER")] {
        let rec = library::record(&dirs, &r.imported[0]).unwrap();
        assert_eq!(rec.manifest_id.clone().unwrap_or(rec.id), manifest);
    }
    assert_eq!(library::list(&dirs, Some(ModKind::Eml)).unwrap().len(), 3);
    // the same mod again is the same entry, not a fourth
    let again = import_into(&dirs, &[one("a2", "ember")]);
    assert_eq!(again.existing.len(), 1);
    assert_eq!(again.existing[0].id, ids[1]);
    assert_eq!(library::list(&dirs, Some(ModKind::Eml)).unwrap().len(), 3);
    // and a new version of "Ember" goes to Ember's entry
    let d = t.0.join("a3");
    put(&d, "m/mod.json", br#"{"id":"Ember","name":"Ember","version":"2.0.0","capabilities":[]}"#);
    put(&d, "m/src/mod.lua", b"return {}");
    let v2 = import_into(&dirs, &[d.join("m")]);
    assert_eq!((v2.imported[0].id.as_str(), v2.imported[0].version.as_str()), ("ember", "2.0.0"));
}

#[test]
fn kilns_own_kinds_keep_the_strict_id() {
    let t = tmp("strict");
    let dirs = DataDirs::at(t.0.join("data"));
    let k = t.0.join("k");
    put(&k, "kiln.mod.json", br#"{ "id": "Night Owl", "name": "Night Owl", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#);
    put(&k, "M.dll", b"x");
    let n = t.0.join("n");
    put(&n, "native.json", br#"{ "format": 1, "id": "FPS Unlock", "version": "1.0.0", "dlls": ["a.dll"] }"#);
    put(&n, "a.dll", &build_test_dll(&["x"], b""));
    let r = import_into(&dirs, &[k, n]);
    assert!(r.imported.is_empty(), "{r:?}");
    assert_eq!(r.skipped.len(), 2);
    for s in &r.skipped {
        assert!(s.reason.contains("not a Kiln mod id") && s.reason.contains("the author has to change it"), "{}", s.reason);
    }
}

#[test]
fn engines_that_name_a_folder_after_the_id_keep_the_strict_id() {
    // ShroudForge and Enshwick: Kiln places or copies the mod into a folder those engines read, named by its id
    let t = tmp("strict-engines");
    let dirs = DataDirs::at(t.0.join("data"));
    let sf = t.0.join("sf");
    put(&sf, "mod.json", br#"{"id":"My SF Mod","name":"My SF Mod","version":"1.0.0"}"#);
    put(&sf, "extended.mod.json", br#"{"launcher":"SF","enabled":true,"targets":["client"]}"#);
    put(&sf, "src/mod.lua", b"return {}");
    let en = t.0.join("en");
    put(&en, "pack.toml", b"id = \"You.Upper\"\n");
    let r = import_into(&dirs, &[sf, en]);
    assert!(r.imported.is_empty(), "{r:?}");
    assert_eq!(r.skipped.len(), 2);
    assert!(r.skipped.iter().any(|s| s.reason.contains("ShroudForge packages under their id")), "{:?}", r.skipped);
    assert!(r.skipped.iter().any(|s| s.reason.contains("Enshwick packs use")), "{:?}", r.skipped);
}

#[test]
fn a_proxy_dll_keeps_the_files_it_reads_beside_it() {
    // Embervale Atlas #186: dinput8.dll + .dat + .jpg + licence, readme and an uninstall script
    let t = tmp("proxy-atlas");
    let d = t.0.join("Embervale Atlas 1.0.0");
    layout(&d, "186-atlas");
    let z = t.0.join("Embervale Atlas 1.0.0.zip");
    zip_of(&d, &z);
    for (how, path) in [("zip", z), ("folder", d)] {
        let (_k, dirs, r) = import(&[path]);
        // the files are kept, and the result says where they are
        assert!(r.skipped.is_empty(), "{how}: {r:?}");
        assert_eq!(r.notes.iter().map(|n| n.text.as_str()).collect::<Vec<_>>(),
            ["Embervale Atlas 1.0.0 is a DLL mod with files: Kiln loads dinput8.dll from its own folder, with those files beside it"], "{how}");
        assert_eq!(found(&r), ["native/embervale-atlas-1.0.0"], "{how}");
        let folder = library::path_of(&dirs, &r.imported[0]).unwrap();
        for f in ["dinput8.dll", "embervale_atlas.dat", "embervale_atlas_map.jpg", "Embervale Atlas - Licenses.txt"] {
            assert!(folder.join(f).is_file(), "{how}: {f} lost");
        }
        let nj: serde_json::Value = serde_json::from_slice(&std::fs::read(folder.join("native.json")).unwrap()).unwrap();
        // a dinput8 proxy is chain-loaded in DllMain, from the folder that holds its files
        assert_eq!((nj["phase"].as_str(), nj["dlls"][0].as_str()), (Some("dllmain"), Some("dinput8.dll")), "{nj}");
        assert_eq!(nj["name"], "Embervale Atlas 1.0.0");
        assert_eq!(library::record(&dirs, &r.imported[0]).unwrap().kind, ModKind::Native);
    }
}

#[test]
fn proxy_dll_shapes_each_become_one_native_mod_named_after_their_package() {
    // (layout, package name, DLL, phase, a data file that must be there)
    let cases = [
        ("154-shroudhud-standalone", "ShroudHud 1.7 - Standalone", "dinput8.dll", "dllmain", "README.txt"),
        ("165-sprint-toggle", "ToggleSprint 0.1", "dinput8.dll", "dllmain", "ToggleSprint.ini"),
        ("149-no-intro", "ENSHROUDEDNo-IntroFix-v.0.1", "winmm.dll", "early", "ReadMe.txt"),
        ("141-dlss-fg", "EnshroudedVKFG Mod", "powrprof.dll", "early", "Streamline/sl.interposer.dll"),
    ];
    let t = tmp("proxy-shapes");
    let dirs = DataDirs::at(t.0.join("data"));
    let mut ids = Vec::new();
    for (which, package, dll, phase, data) in cases {
        let d = t.0.join(which);
        layout(&d, which);
        let z = t.0.join(format!("{package}.zip"));
        zip_of(&d, &z);
        let r = import_into(&dirs, &[z]);
        assert!(r.skipped.is_empty(), "{which}: {r:?}");
        // only a package with files the DLL may read says where they are (documents alone are no files)
        assert_eq!(r.notes.len(), usize::from(["165-sprint-toggle", "141-dlss-fg"].contains(&which)), "{which}: {:?}", r.notes);
        assert_eq!(r.imported.len(), 1, "{which}: {r:?}");
        ids.push(r.imported[0].id.clone());
        let folder = library::path_of(&dirs, &r.imported[0]).unwrap();
        assert!(folder.join(data).is_file(), "{which}: {data} lost");
        let nj: serde_json::Value = serde_json::from_slice(&std::fs::read(folder.join("native.json")).unwrap()).unwrap();
        assert_eq!((nj["phase"].as_str(), nj["dlls"][0].as_str()), (Some(phase), Some(dll)), "{which}: {nj}");
        assert_eq!(nj["name"], package);
    }
    // Shroudscaler #113 is also a lone winmm.dll: a different package, a different entry
    let d = t.0.join("113-shroudscaler");
    layout(&d, "113-shroudscaler");
    put(&d, "winmm.dll", &build_test_dll(&["timeGetTime"], b"another proxy"));
    let z = t.0.join("Winmm.zip");
    zip_of(&d, &z);
    let r = import_into(&dirs, &[z]);
    assert_eq!(found(&r), ["native/winmm"]);
    assert!(!ids.contains(&"winmm".to_string()));
    assert_eq!(library::list(&dirs, Some(ModKind::Native)).unwrap().len(), 5);
}

#[test]
fn a_lone_proxy_dll_gets_an_id_that_two_different_dlls_do_not_share() {
    let t = tmp("lone-proxy");
    let dirs = DataDirs::at(t.0.join("a"));
    put(&t.0, "one/winmm.dll", &plain_winmm());
    put(&t.0, "two/winmm.dll", &build_test_dll(&["timeGetTime"], b"another one"));
    let r = import_into(&dirs, &[t.0.join("one/winmm.dll"), t.0.join("two/winmm.dll")]);
    assert_eq!(r.imported.len(), 2, "{r:?}");
    assert_ne!(r.imported[0].id, r.imported[1].id);
    assert!(r.imported.iter().all(|x| x.id.starts_with("winmm-")));
    // the same DLL twice is one entry
    let again = import_into(&dirs, &[t.0.join("one/winmm.dll")]);
    assert_eq!(again.existing.len(), 1);
}

#[test]
fn a_tools_package_is_left_out_whole_with_one_reason() {
    // Mod Menu #152: Aldrich's powrprof.dll and its layer folder are no mods to run next to Kiln
    let (_t, ways) = ways("152-mod-menu");
    for (how, path) in ways {
        let (_k, _d, r) = import(&[path]);
        assert!(r.imported.is_empty(), "{how}: its layer DLL is not a mod on its own: {r:?}");
        assert_eq!(r.skipped.len(), 1, "{how}: {:?}", r.skipped);
        assert!(r.skipped[0].reason.contains("Aldrich Mod Menu") && r.skipped[0].reason.contains("copy its files there yourself"), "{}", r.skipped[0].reason);
    }
}

#[test]
fn reasons_are_one_sentence_a_player_can_read() {
    use kiln_manage::engines::{loader_reason, Engine};
    for e in [Engine::Eml, Engine::Emf, Engine::ShroudForge, Engine::Set, Engine::Greeneye, Engine::Flame, Engine::Shroudtopia,
        Engine::Enshwick, Engine::Shroudlink, Engine::Ember, Engine::ModLoaderProxy, Engine::ChainLoader, Engine::LiveMap, Engine::SharedXp,
        Engine::ReShade, Engine::Aldrich, Engine::UltimateAsiLoader, Engine::Kiln] {
        let r = loader_reason(e, "x.dll");
        assert!(!r.ends_with('.') && !r.contains(". ") && r.len() < 160, "{e:?}: {r}");
        assert!(!r.contains("not a mod to import") && !r.contains("chains it when installed over"), "{e:?}: {r}");
    }
}

#[test]
fn a_folder_of_docs_and_screenshots_does_not_add_noise() {
    let t = tmp("noise");
    let d = t.0.join("pkg");
    eml_mod(&d, "mods/m", "m", "M");
    put(&d, "fomod/ModuleConfig.xml", b"<config/>");
    put(&d, "screenshots/a.jpg", b"jpg");
    put(&d, "__MACOSX/x", b"x");
    let (_k, _d, r) = import(&[d]);
    assert_eq!(found(&r), ["eml/m"]);
    assert!(r.skipped.is_empty(), "{:?}", r.skipped);
}

// ---- 7z ----

#[cfg(feature = "sevenz")]
mod sevenz {
    use super::*;
    use sevenz_rust2::{ArchiveEntry, ArchiveWriter};

    /// A 7z of `dir` (entry names relative to it).
    pub fn sevenz_of(dir: &Path, out: &Path) {
        let mut w = ArchiveWriter::create(out).unwrap();
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
            let data = std::fs::read(&f).unwrap();
            w.push_archive_entry(ArchiveEntry::new_file(&name), Some(&data[..])).unwrap();
        }
        w.finish().unwrap();
    }

    fn entry_archive(out: &Path, entries: Vec<(ArchiveEntry, Vec<u8>)>) {
        let mut w = ArchiveWriter::create(out).unwrap();
        for (e, data) in entries {
            w.push_archive_entry(e, Some(&data[..])).unwrap();
        }
        w.finish().unwrap();
    }

    #[test]
    fn a_7z_imports_like_its_zip() {
        for which in ["136-eml-included", "43-shroudtopia", "186-atlas"] {
            let t = tmp(&format!("7z-{which}"));
            let d = t.0.join(which);
            layout(&d, which);
            let z = t.0.join(format!("{which}.zip"));
            zip_of(&d, &z);
            let s = t.0.join(format!("{which}.7z"));
            sevenz_of(&d, &s);
            let (_k1, _d1, from_zip) = import(&[z]);
            let (_k2, _d2, from_7z) = import(&[s.clone()]);
            assert_eq!(found(&from_7z), found(&from_zip), "{which}");
            assert!(!found(&from_7z).is_empty());
            assert_eq!(from_7z.skipped.iter().map(|s| &s.reason).collect::<Vec<_>>(), from_zip.skipped.iter().map(|s| &s.reason).collect::<Vec<_>>());
            assert_eq!(from_7z.notes.iter().map(|n| &n.text).collect::<Vec<_>>(), from_zip.notes.iter().map(|n| &n.text).collect::<Vec<_>>());
            // the same bytes: kdh1 of an entry is the same from either package
            let mut h1: Vec<_> = from_zip.imported.iter().map(|r| r.hash.clone()).collect();
            let mut h2: Vec<_> = from_7z.imported.iter().map(|r| r.hash.clone()).collect();
            h1.sort();
            h2.sort();
            assert_eq!(h1, h2, "{which}");
            // detect reads it too, with the archive as the path
            let det = library::detect(&s).unwrap();
            assert!(det.path.ends_with(&format!("{which}.7z")));
            assert!(det.items.iter().any(|i| i.kind.importable()) || det.kind.importable());
        }
    }

    #[test]
    fn a_7z_with_unsafe_entries_is_refused() {
        let t = tmp("7z-unsafe");
        let mut link = ArchiveEntry::new_file("mods/evil/link");
        link.has_windows_attributes = true;
        link.windows_attributes = 0x8000 | (0o120777 << 16);
        let cases: Vec<(&str, Vec<(ArchiveEntry, Vec<u8>)>)> = vec![
            ("traversal", vec![(ArchiveEntry::new_file("../evil.txt"), b"x".to_vec())]),
            ("absolute", vec![(ArchiveEntry::new_file("/etc/evil"), b"x".to_vec())]),
            ("link", vec![(link, b"/etc/passwd".to_vec())]),
            ("modpack", vec![(ArchiveEntry::new_file("kiln-modpack.json"), b"{}".to_vec())]),
        ];
        for (name, entries) in cases {
            let s = t.0.join(format!("{name}.7z"));
            entry_archive(&s, entries);
            let dirs = DataDirs::at(t.0.join(format!("data-{name}")));
            let e = library::import(&dirs, &[s.clone()], &mut NoProgress).unwrap_err();
            assert!(matches!(e.code, kiln_manage::ErrorCode::Refused | kiln_manage::ErrorCode::ConsentRequired), "{name}: {e:?}");
            assert!(kiln_manage::quick::validate_import_path(&s).is_err(), "{name}");
            assert!(!t.0.join("evil.txt").exists());
            assert!(library::list(&dirs, None).unwrap().is_empty());
        }
    }

    #[test]
    fn a_7z_over_the_entry_limit_is_refused_and_a_damaged_one_is_skipped_with_its_reason() {
        let t = tmp("7z-limits");
        let many: Vec<(ArchiveEntry, Vec<u8>)> = (0..=kiln_manage::quick::MAX_IMPORT_ENTRIES)
            .map(|i| (ArchiveEntry::new_file(&format!("f/{i}.txt")), b"x".to_vec())).collect();
        let s = t.0.join("many.7z");
        entry_archive(&s, many);
        assert_eq!(kiln_manage::quick::validate_import_path(&s).unwrap_err().code, kiln_manage::ErrorCode::Refused);
        // not a 7z at all: the import goes on without it and says why
        let bad = t.0.join("bad.7z");
        std::fs::write(&bad, b"this is not a 7z").unwrap();
        assert!(kiln_manage::quick::validate_import_path(&bad).is_err());
        let ok = t.0.join("ok");
        eml_mod(&ok, "m", "m", "M");
        let dirs = DataDirs::at(t.0.join("data"));
        let e = library::import(&dirs, &[bad.clone(), ok.join("m")], &mut NoProgress);
        // validate_import_path is the launcher's check before an import; a direct library call reports the file
        let r = e.unwrap();
        assert_eq!(found(&r), ["eml/m"]);
        assert_eq!(r.skipped.len(), 1);
        assert!(r.skipped[0].reason.contains("not a readable 7z"), "{}", r.skipped[0].reason);
    }
}

// ---- rar, through a tool that is on the machine ----

mod rar {
    use super::*;

    fn vint(mut n: u64) -> Vec<u8> {
        let mut o = Vec::new();
        loop {
            let b = (n & 0x7f) as u8;
            n >>= 7;
            if n == 0 { o.push(b); break; }
            o.push(b | 0x80);
        }
        o
    }

    fn crc32(data: &[u8]) -> u32 {
        let mut c = 0xffff_ffffu32;
        for &b in data {
            c ^= b as u32;
            for _ in 0..8 {
                c = if c & 1 != 0 { (c >> 1) ^ 0xedb8_8320 } else { c >> 1 };
            }
        }
        !c
    }

    fn block(body: Vec<u8>) -> Vec<u8> {
        let size = vint(body.len() as u64);
        let mut covered = size.clone();
        covered.extend(&body);
        let mut out = crc32(&covered).to_le_bytes().to_vec();
        out.extend(covered);
        out
    }

    /// A RAR5 archive that stores its files (method 0), by hand: signature, main header, one file header + data each, end.
    pub fn rar5_stored(files: &[(&str, &[u8])]) -> Vec<u8> {
        let mut out = b"Rar!\x1a\x07\x01\x00".to_vec();
        let mut main = vint(1);
        main.extend(vint(0));
        main.extend(vint(0));
        out.extend(block(main));
        for (name, data) in files {
            let mut h = vint(2);
            h.extend(vint(0x0002)); // header flags: a data area
            h.extend(vint(data.len() as u64)); // data size
            h.extend(vint(0x0004)); // file flags: CRC32 present
            h.extend(vint(data.len() as u64)); // unpacked size
            h.extend(vint(0o100644)); // attributes
            h.extend(crc32(data).to_le_bytes());
            h.extend(vint(0)); // compression: version 0, method 0 (store)
            h.extend(vint(1)); // host OS: unix
            h.extend(vint(name.len() as u64));
            h.extend(name.as_bytes());
            out.extend(block(h));
            out.extend(*data);
        }
        let mut end = vint(5);
        end.extend(vint(0));
        end.extend(vint(0));
        out.extend(block(end));
        out
    }

    /// The tool the library would use, or None (the tests then say so and pass).
    fn have_tool() -> bool {
        ["unrar", "7zz", "7z", "bsdtar"].iter().any(|n| {
            std::env::var_os("PATH").is_some_and(|p| std::env::split_paths(&p).any(|d| d.join(n).is_file()))
        })
    }

    #[test]
    fn a_rar_imports_like_its_zip_when_a_tool_is_there() {
        if !have_tool() {
            eprintln!("skipped: no unrar, 7z or bsdtar on this machine");
            return;
        }
        let t = tmp("rar");
        let files: Vec<(&str, Vec<u8>)> = vec![
            ("dbghelp.dll", eml_dbghelp()),
            ("mods/HoardersHelper/mod.json", eml_json("HoardersHelper", "Hoarder's Helper")),
            ("mods/HoardersHelper/src/mod.lua", b"return {}".to_vec()),
        ];
        let borrowed: Vec<(&str, &[u8])> = files.iter().map(|(n, d)| (*n, d.as_slice())).collect();
        let r = t.0.join("Hoarders Helper.rar");
        std::fs::write(&r, rar5_stored(&borrowed)).unwrap();
        let dirs = DataDirs::at(t.0.join("data"));
        let rep = library::import(&dirs, &[r.clone()], &mut NoProgress).unwrap();
        if rep.skipped.iter().any(|s| s.reason.contains("couldn't unpack")) {
            // a tool that does not read RAR5 (an old p7zip): not this code's failure
            eprintln!("skipped: the tool on this machine cannot read RAR5: {:?}", rep.skipped);
            return;
        }
        assert_eq!(found(&rep), ["eml/hoardershelper"], "{rep:?}");
        assert_eq!(rep.notes.len(), 1);
        let folder = library::path_of(&dirs, &rep.imported[0]).unwrap();
        assert_eq!(std::fs::read(folder.join("src/mod.lua")).unwrap(), b"return {}");
        // nothing was left in the staging folder
        let left: Vec<_> = std::fs::read_dir(dirs.library()).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).filter(|n| n.starts_with('.')).collect();
        assert!(left.is_empty(), "{left:?}");
    }

    #[test]
    fn a_rar_path_that_climbs_out_is_stopped_by_the_tool_or_the_check() {
        if !have_tool() {
            eprintln!("skipped: no unrar, 7z or bsdtar on this machine");
            return;
        }
        let t = tmp("rar-evil");
        std::fs::create_dir_all(t.0.join("work")).unwrap();
        let r = t.0.join("work/evil.rar");
        std::fs::write(&r, rar5_stored(&[("../../escaped.txt", b"x"), ("mods/m/mod.json", b"{}")])).unwrap();
        let dirs = DataDirs::at(t.0.join("data"));
        let _ = library::import(&dirs, &[r], &mut NoProgress);
        assert!(!t.0.join("escaped.txt").exists() && !t.0.parent().unwrap().join("escaped.txt").exists());
        assert!(!t.0.join("work/escaped.txt").exists());
    }

    #[test]
    fn a_damaged_rar_is_skipped_with_what_to_do() {
        if !have_tool() {
            eprintln!("skipped: no unrar, 7z or bsdtar on this machine");
            return;
        }
        let t = tmp("rar-bad");
        let r = t.0.join("bad.rar");
        std::fs::write(&r, b"Rar!\x1a\x07\x01\x00 and then nothing useful").unwrap();
        let dirs = DataDirs::at(t.0.join("data"));
        let rep = library::import(&dirs, &[r], &mut NoProgress).unwrap();
        assert!(rep.imported.is_empty());
        assert_eq!(rep.skipped.len(), 1);
        assert!(rep.skipped[0].reason.contains("extract it first, then drop the folder"), "{}", rep.skipped[0].reason);
    }
}
