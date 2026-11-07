//! Adversarial import cases for the engine importers, including direct library callers.
use kiln_manage::{ErrorCode, hash, library, manifest::ModKind, paths::DataDirs, pe::build_test_dll, quick, task::NoProgress};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp { fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.0); } }
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-engine-import-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}
fn put(root: &Path, name: &str, bytes: &[u8]) {
    let p = root.join(name);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, bytes).unwrap();
}
fn set_mod(root: &Path, id: &str, version: &str) {
    put(root, "init.lua", b"return {}");
    put(root, "mod.json", serde_json::to_vec(&serde_json::json!({"id":id,"version":version})).unwrap().as_slice());
}

#[test]
fn direct_library_call_refuses_oversized_and_deep_engine_folders() {
    let t = tmp("limits");
    let src = t.0.join("set");
    set_mod(&src, "setmod", "1.0.0");
    let huge = src.join("huge");
    std::fs::File::create(&huge).unwrap().set_len(quick::MAX_IMPORT_FILE + 1).unwrap();
    let dirs = DataDirs::at(t.0.join("data"));
    assert!(library::import(&dirs, &[src.clone()], &mut NoProgress).is_err());
    assert!(library::detect(&src).is_err());
    std::fs::remove_file(huge).unwrap();
    let mut deep = src.clone();
    for _ in 0..=quick::MAX_IMPORT_DEPTH { deep.push("d"); }
    std::fs::create_dir_all(&deep).unwrap();
    assert!(library::import(&dirs, &[src], &mut NoProgress).is_err());
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[cfg(unix)]
#[test]
fn all_folder_links_and_lone_dll_links_are_refused() {
    use std::os::unix::fs::symlink;
    let t = tmp("links");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("set");
    set_mod(&src, "setmod", "1.0.0");
    put(&t.0, "outside.lua", b"return {}");
    for name in ["asset.lua", ".hidden"] {
        symlink(t.0.join("outside.lua"), src.join(name)).unwrap();
        let result = library::import(&dirs, &[src.clone()], &mut NoProgress);
        assert!(result.is_err() || result.unwrap().imported.is_empty(), "{name}");
        std::fs::remove_file(src.join(name)).unwrap();
    }
    put(&t.0, "real.dll", &build_test_dll(&["GreeneyeModStart"], b""));
    symlink(t.0.join("real.dll"), t.0.join("link.dll")).unwrap();
    let result = library::import(&dirs, &[t.0.join("link.dll")], &mut NoProgress);
    assert!(result.is_err() || result.unwrap().imported.is_empty());
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[test]
fn invalid_manifest_ids_are_refused_and_fallback_versions_are_valid() {
    let t = tmp("identity");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("set");
    for id in ["", "../escape", "bad\nid", "trailing\n", "Uppercase"] {
        set_mod(&src, id, "1.0.0");
        let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
        assert!(r.imported.is_empty() && !r.skipped.is_empty(), "{id:?}: {r:?}");
    }
    for v in ["001", "../../escape", "1.0/evil", "???", "beta"] {
        set_mod(&src, "setmod", v);
        let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
        let m = r.imported.first().or_else(|| r.existing.first()).unwrap();
        assert!(kiln_manage::semver::SemVersion::parse(&m.version).is_ok(), "{v}: {}", m.version);
        assert!(library::path_of(&dirs, m).unwrap().starts_with(dirs.library().join("set/setmod")));
        if v == "beta" { assert_eq!(m.version, "0.0.0-beta", "preserve earlier valid fallback identities"); }
    }
}

#[test]
fn engine_dll_folders_get_the_manifests_their_plugins_need() {
    let t = tmp("folder-manifest");
    let dirs = DataDirs::at(t.0.join("data"));
    for (name, export, kind, manifest) in [
        ("greeneye", "GreeneyeModStart", ModKind::Greeneye, "mod.json"),
        ("flame", "FlameStartRuntime", ModKind::Flame, "flame.json"),
        ("shroudtopia", "CreateModInstance", ModKind::Shroudtopia, "mod.json"),
    ] {
        let src = t.0.join(name);
        put(&src, "payload.dll", &build_test_dll(&[export], b""));
        put(&src, "assets/data", b"preserved");
        let first = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
        let r = &first.imported[0];
        assert_eq!(r.kind, kind);
        let folder = library::path_of(&dirs, r).unwrap();
        assert!(folder.join(manifest).is_file(), "{kind:?}");
        if kind != ModKind::Shroudtopia {
            let generated: serde_json::Value = serde_json::from_slice(&std::fs::read(folder.join(manifest)).unwrap()).unwrap();
            assert_eq!(generated["dll"], "payload.dll");
        }
        assert_eq!(std::fs::read(folder.join("assets/data")).unwrap(), b"preserved");
        assert_eq!(hash::dirhash(&folder).unwrap(), r.hash);
        assert_eq!(library::import(&dirs, &[src], &mut NoProgress).unwrap().existing, [r.clone()]);
    }
}

#[test]
fn existing_address_must_have_matching_full_hash_and_content() {
    let t = tmp("address");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("set");
    set_mod(&src, "setmod", "1.0.0");
    let first = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap().imported.remove(0);
    let folder = library::path_of(&dirs, &first).unwrap();
    let record = folder.with_file_name(format!("{}.json", folder.file_name().unwrap().to_str().unwrap()));
    let original_record = std::fs::read(&record).unwrap();
    let mut wrong: library::ImportRecord = serde_json::from_slice(&original_record).unwrap();
    let last = wrong.hash.pop().unwrap();
    wrong.hash.push(if last == '0' { '1' } else { '0' });
    std::fs::write(&record, serde_json::to_vec(&wrong).unwrap()).unwrap();
    let r = library::import(&dirs, &[src.clone()], &mut NoProgress).unwrap();
    assert!(r.existing.is_empty() && r.imported.is_empty() && !r.skipped.is_empty());
    std::fs::write(&record, original_record).unwrap();
    put(&folder, "init.lua", b"modified behind library's back");
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    assert!(r.existing.is_empty() && r.imported.is_empty() && !r.skipped.is_empty());
    assert_eq!(std::fs::read(folder.join("init.lua")).unwrap(), b"modified behind library's back");
}

#[test]
fn equivalent_folders_import_to_the_same_content_address() {
    let t = tmp("determinism");
    let dirs = DataDirs::at(t.0.join("data"));
    let a = t.0.join("a");
    let b = t.0.join("b");
    set_mod(&a, "setmod", "1.0.0");
    put(&a, "z.lua", b"z");
    put(&a, "a.lua", b"a");
    put(&b, "a.lua", b"a");
    put(&b, "z.lua", b"z");
    set_mod(&b, "setmod", "1.0.0");
    put(&b, ".ignored", b"different");
    let first = library::import(&dirs, &[a], &mut NoProgress).unwrap().imported.remove(0);
    let second = library::import(&dirs, &[b], &mut NoProgress).unwrap();
    assert_eq!(second.existing, [first.clone()]);
    assert_eq!(first.kind, ModKind::Set);
    assert_eq!(hash::dirhash(&library::path_of(&dirs, &first).unwrap()).unwrap(), first.hash);
}

#[test]
fn library_references_cannot_escape_their_content_address() {
    let t = tmp("refs");
    let dirs = DataDirs::at(t.0.join("data"));
    let hash = format!("kdh1:{}", "0".repeat(64));
    for (id, version) in [("../escape", "1.0.0"), ("setmod", "../../../escape"), ("setmod\n", "1.0.0"), ("setmod", "1.0.0\n")] {
        let r = library::LibraryRef { kind: ModKind::Set, id: id.into(), version: version.into(), hash: hash.clone() };
        assert!(library::path_of(&dirs, &r).is_err());
        assert!(library::record(&dirs, &r).is_err());
        assert!(library::remove(&dirs, &r, true).is_err());
    }
    let r = library::LibraryRef { kind: ModKind::Set, id: "setmod".into(), version: "1.0.0".into(), hash: "0".repeat(64) };
    assert!(library::path_of(&dirs, &r).is_err());
}

#[test]
fn loader_cannot_commit_a_folder_under_an_incorrect_hash() {
    let t = tmp("commit-hash");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("set");
    set_mod(&src, "setmod", "1.0.0");
    let mut report = library::ImportReport::default();
    let rec = library::ImportRecord {
        format: 1, kind: ModKind::Set, id: "setmod".into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "0".repeat(64)),
        imported: "now".into(), source: None, size: 0, name: None, side: None, clients: None, file: None,
    };
    assert_eq!(library::commit_folder(&dirs, &src, rec, &mut report).unwrap_err().code, ErrorCode::HashMismatch);
    assert!(report.imported.is_empty());
    assert!(src.is_dir());
}

#[test]
fn plugin_record_cannot_name_a_dll_outside_its_folder() {
    let t = tmp("plugin-path");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("plugin.dll");
    std::fs::write(&src, build_test_dll(&["kiln_plugin_main"], b"")).unwrap();
    let r = library::import(&dirs, &[src], &mut NoProgress).unwrap().imported.remove(0);
    let folder = library::path_of(&dirs, &r).unwrap().parent().unwrap().to_path_buf();
    let rec_path = folder.with_file_name(format!("{}.json", folder.file_name().unwrap().to_str().unwrap()));
    let mut rec = library::record(&dirs, &r).unwrap();
    for file in ["../outside.dll", "C:/outside.dll", "nested/helper.dll", "..\\outside.dll"] {
        rec.file = Some(file.into());
        std::fs::write(&rec_path, serde_json::to_vec(&rec).unwrap()).unwrap();
        assert!(library::path_of(&dirs, &r).is_err(), "{file}");
    }
}

#[cfg(feature = "zip")]
#[test]
fn direct_library_zip_limits_and_links_are_refused_before_extraction() {
    let t = tmp("zip");
    let dirs = DataDirs::at(t.0.join("data"));
    let path = t.0.join("deep.zip");
    let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    let name = format!("{}init.lua", "d/".repeat(quick::MAX_IMPORT_DEPTH));
    z.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
    std::io::Write::write_all(&mut z, b"return {}").unwrap();
    z.finish().unwrap();
    assert_eq!(library::import(&dirs, &[path.clone()], &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    assert!(library::detect(&path).is_err());
    #[cfg(unix)] {
        let link = t.0.join("link.zip");
        std::os::unix::fs::symlink(&path, &link).unwrap();
        let r = library::import(&dirs, &[link], &mut NoProgress);
        assert!(r.is_err() || r.unwrap().imported.is_empty());
    }
    let path = t.0.join("many.zip");
    let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    for i in 0..=quick::MAX_IMPORT_ENTRIES {
        z.start_file(i.to_string(), zip::write::SimpleFileOptions::default()).unwrap();
    }
    z.finish().unwrap();
    assert_eq!(library::import(&dirs, &[path], &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    let path = t.0.join("bomb-size.zip");
    let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    z.start_file("init.lua", zip::write::SimpleFileOptions::default()).unwrap();
    std::io::Write::write_all(&mut z, b"return {}").unwrap();
    z.finish().unwrap();
    let mut bytes = std::fs::read(&path).unwrap();
    let cd = bytes.windows(4).position(|w| w == b"PK\x01\x02").unwrap();
    bytes[cd + 24..cd + 28].copy_from_slice(&((quick::MAX_IMPORT_FILE + 1) as u32).to_le_bytes());
    std::fs::write(&path, bytes).unwrap();
    assert_eq!(library::import(&dirs, &[path], &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    assert!(library::list(&dirs, None).unwrap().is_empty());
}

#[cfg(feature = "zip")]
#[test]
fn engine_zip_traversal_and_embedded_links_are_refused() {
    let t = tmp("zip-links");
    let dirs = DataDirs::at(t.0.join("data"));
    for (i, name) in ["../escape.lua", "..\\escape.lua", "/escape.lua", "C:/escape.lua"].into_iter().enumerate() {
        let path = t.0.join(format!("traversal{i}.zip"));
        let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
        z.start_file("init.lua", zip::write::SimpleFileOptions::default()).unwrap();
        std::io::Write::write_all(&mut z, b"return {}").unwrap();
        z.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
        std::io::Write::write_all(&mut z, b"outside").unwrap();
        z.finish().unwrap();
        assert!(library::import(&dirs, &[path], &mut NoProgress).is_err(), "{name}");
    }
    let path = t.0.join("symlink.zip");
    let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    z.start_file("init.lua", zip::write::SimpleFileOptions::default()).unwrap();
    std::io::Write::write_all(&mut z, b"return {}").unwrap();
    z.add_symlink("asset.lua", "../outside", zip::write::SimpleFileOptions::default()).unwrap();
    z.finish().unwrap();
    assert_eq!(library::import(&dirs, &[path], &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    assert!(!t.0.join("escape.lua").exists());
    assert!(library::list(&dirs, None).unwrap().is_empty());
}
