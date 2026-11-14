//! Source receipts through the real library import: an imported archive binds every entry it gave to the archive's
//! digest, a folder binds nothing, and the coordinator reads a mod's source from the manifest in its library folder.
#![cfg(feature = "zip")]

use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::task::NoProgress;
use kiln_manage::updates::coordinator::{self, CheckOptions};
use kiln_manage::updates::receipt::{self, ReceiptProvider};
use kiln_manage::updates::{Provider, SourceOrigin, UpdateState};
use std::io::Write;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn tmp(tag: &str) -> Tmp {
    let p = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("updates-receipts-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}

fn zip_of(path: &Path, files: &[(&str, &str)]) {
    let mut z = zip::ZipWriter::new(std::fs::File::create(path).unwrap());
    for (name, text) in files {
        z.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
        z.write_all(text.as_bytes()).unwrap();
    }
    z.finish().unwrap();
}

fn kiln_manifest(id: &str, version: &str, extra: &str) -> String {
    format!(r#"{{"id": "{id}", "name": "{id}", "version": "{version}", "apiVersion": "0.2"{extra}}}"#)
}

fn by_id<'a>(refs: &'a [LibraryRef], id: &str) -> &'a LibraryRef {
    refs.iter().find(|r| r.id == id).unwrap_or_else(|| panic!("{id} was not imported: {refs:?}"))
}

#[test]
fn an_imported_archive_binds_every_entry_it_gave_and_a_folder_binds_nothing() {
    let t = tmp("import");
    let dirs = DataDirs::at(t.0.join("data"));
    // one zip with two mods: a Kiln mod and a native mod
    let zip = t.0.join("glow-bundle-1.2.0.zip");
    zip_of(&zip, &[
        ("lantern-glow/kiln.mod.json", &kiln_manifest("lantern-glow", "1.2.0", "")),
        ("lantern-glow/notes.txt", "hello"),
        ("glow-native/native.json", r#"{"id": "glow-native", "version": "0.3.0", "dlls": ["glow.dll"]}"#),
        ("glow-native/glow.dll", "not really a dll"),
    ]);
    let digest = kiln_manage::hash::sha256_file(&zip).unwrap().0;
    let report = library::import(&dirs, std::slice::from_ref(&zip), &mut NoProgress).unwrap();
    assert_eq!(report.imported.len(), 2, "{report:?}");
    let (glow, native) = (by_id(&report.imported, "lantern-glow").clone(), by_id(&report.imported, "glow-native").clone());
    assert_eq!((glow.kind, native.kind), (ModKind::Kiln, ModKind::Native));
    let file = receipt::load(&dirs).unwrap();
    assert_eq!(file.receipts.len(), 2);
    for r in [&glow, &native] {
        let x = file.receipt(r).unwrap_or_else(|| panic!("no receipt for {}", r.id));
        assert_eq!((x.provider, x.artifact_sha256.as_deref()), (ReceiptProvider::Local, Some(digest.as_str())), "{}", r.id);
        assert_eq!((x.release_id.as_ref(), x.asset_id.as_ref(), x.file_chain_id.as_ref()), (None, None, None));
        assert_eq!((x.mod_id.as_str(), x.installed_version.as_str()), (r.id.as_str(), r.version.as_str()));
    }
    // the receipt file holds no path of this PC (the import record does; the receipt is about bytes)
    let text = std::fs::read_to_string(receipt::path_of(&dirs)).unwrap();
    assert!(!text.contains(&t.0.display().to_string()) && !text.contains("glow-bundle"), "{text}");

    // a mod copied in as a folder: imported, and nothing is claimed about where its bytes came from
    let folder = t.0.join("src/night-sky");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(folder.join("kiln.mod.json"), kiln_manifest("night-sky", "2.0.0", "")).unwrap();
    let report = library::import(&dirs, std::slice::from_ref(&folder), &mut NoProgress).unwrap();
    let sky = by_id(&report.imported, "night-sky").clone();
    let file = receipt::load(&dirs).unwrap();
    assert!(file.receipt(&sky).is_none(), "a folder import has no archive to bind");
    assert_eq!(file.receipts.len(), 2);

    // the same bytes again in another archive (the same mod zipped by itself): the entry exists already, and its
    // receipt names the archive seen last; the other entry keeps the first archive's digest
    let again = t.0.join("lantern-glow-1.2.0.zip");
    zip_of(&again, &[("lantern-glow/kiln.mod.json", &kiln_manifest("lantern-glow", "1.2.0", "")), ("lantern-glow/notes.txt", "hello")]);
    let digest2 = kiln_manage::hash::sha256_file(&again).unwrap().0;
    assert_ne!(digest, digest2);
    let report = library::import(&dirs, std::slice::from_ref(&again), &mut NoProgress).unwrap();
    assert_eq!((report.imported.len(), report.existing.as_slice()), (0, std::slice::from_ref(&glow)));
    let file = receipt::load(&dirs).unwrap();
    assert_eq!(file.receipt(&glow).unwrap().artifact_sha256.as_deref(), Some(digest2.as_str()));
    assert_eq!(file.receipt(&native).unwrap().artifact_sha256.as_deref(), Some(digest.as_str()));

    // an import still works when the receipts cannot be written (a newer Kiln's file is left alone)
    let newer = r#"{"format": 2, "policyVersion": 9, "receipts": []}"#;
    std::fs::write(receipt::path_of(&dirs), newer).unwrap();
    let third = t.0.join("extra-1.0.0.zip");
    zip_of(&third, &[("extra/kiln.mod.json", &kiln_manifest("extra", "1.0.0", ""))]);
    let report = library::import(&dirs, std::slice::from_ref(&third), &mut NoProgress).unwrap();
    assert_eq!(report.imported.len(), 1);
    assert_eq!(std::fs::read_to_string(receipt::path_of(&dirs)).unwrap(), newer);
}

#[test]
fn the_coordinator_reads_each_mods_source_from_its_library_folder() {
    let t = tmp("targets");
    let dirs = DataDirs::at(t.0.join("data"));
    let src = t.0.join("src");
    let write = |dir: &str, file: &str, text: &str| {
        std::fs::create_dir_all(src.join(dir)).unwrap();
        std::fs::write(src.join(dir).join(file), text).unwrap();
        src.join(dir)
    };
    let folders = vec![
        // declares a GitHub repository
        write("glow", "kiln.mod.json", &kiln_manifest("lantern-glow", "1.2.0", r#", "updates": {"provider": "github", "repository": "example-org/lantern-glow", "asset": "lantern-glow-{version}.zip"}"#)),
        // declares nothing, but has a homepage: no source is guessed from it
        write("plain", "kiln.mod.json", &kiln_manifest("plain", "0.1.0", r#", "url": "https://github.com/example-org/plain""#)),
        // an "updates" Kiln cannot read: the mod is imported like any other, and has no source
        write("broken", "kiln.mod.json", &kiln_manifest("broken", "0.1.0", r#", "updates": {"provider": "github", "repository": "https://github.com/example-org/broken"}"#)),
        // a native mod declares one too
        write("native", "native.json", r#"{"id": "glow-native", "version": "0.3.0", "dlls": ["glow.dll"], "updates": {"provider": "nexus", "gameId": 5811, "modId": 310, "fileChainId": "90310"}}"#),
        // two versions of one mod: the newest is the installed one
        write("old", "kiln.mod.json", &kiln_manifest("twice", "1.0.0", "")),
        write("new", "kiln.mod.json", &kiln_manifest("twice", "1.1.0", r#", "updates": {"provider": "github", "repository": "example-org/twice"}"#)),
    ];
    std::fs::write(src.join("native/glow.dll"), "x").unwrap();
    let report = library::import(&dirs, &folders, &mut NoProgress).unwrap();
    assert_eq!((report.imported.len(), report.skipped.len()), (6, 0), "{report:?}");

    let targets = coordinator::targets(&dirs).unwrap();
    assert_eq!(targets.iter().map(|t| (t.library_ref.id.as_str(), t.library_ref.version.as_str())).collect::<Vec<_>>(),
        [("broken", "0.1.0"), ("lantern-glow", "1.2.0"), ("plain", "0.1.0"), ("twice", "1.1.0"), ("glow-native", "0.3.0")]);
    // no provider is given: a mod with a source is "disabled" (nobody to ask), one without is "unsupported"
    let opts = CheckOptions { offline: true, refresh: false, automatic: false, now: 1_800_000_000, api_version: "0.2.3".into() };
    let r = coordinator::check(&dirs, &[], &opts, &mut NoProgress).unwrap();
    assert_eq!(r.mods.len(), 5);
    let of = |id: &str| r.mods.iter().find(|m| m.library_ref.id == id).unwrap();
    for (id, provider, destination) in [("lantern-glow", Provider::Github, "example-org/lantern-glow"), ("twice", Provider::Github, "example-org/twice"),
        ("glow-native", Provider::Nexus, "enshrouded/310/90310")] {
        let s = of(id);
        let source = s.source.as_ref().unwrap_or_else(|| panic!("{id} has no source"));
        assert_eq!((s.state, source.provider, source.destination.as_str(), source.origin), (UpdateState::Disabled, provider, destination, SourceOrigin::Manifest), "{id}");
    }
    for id in ["plain", "broken"] {
        assert_eq!((of(id).state, of(id).source.as_ref()), (UpdateState::Unsupported, None), "{id}");
    }
    // the sources seen are the accepted ones from now on
    let file = receipt::load(&dirs).unwrap();
    assert_eq!(file.sources.len(), 3);
    assert_eq!(file.source(ModKind::Kiln, "twice").unwrap().identity.destination, "example-org/twice");
}
