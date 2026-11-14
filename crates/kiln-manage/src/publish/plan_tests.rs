//! Planning is offline and what it freezes cannot be changed unnoticed.

use super::*;
use std::io::Write;

const MANIFEST: &str = r#"{"id": "good-mod", "version": "1.2.3", "apiVersion": "0.2", "side": "client", "entryAssembly": "GoodMod.dll"}"#;
const COMMIT: &str = "0123456789abcdef0123456789abcdef01234567";

struct Fixture {
    root: PathBuf,
    data: PathBuf,
    zip: PathBuf,
    config: PathBuf,
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Ok(rd) = std::fs::read_dir(txdir::transactions(&self.data)) {
            rd.flatten().for_each(|e| txdir::discard(&e.path()));
        }
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn zip_of(path: &Path, files: &[(&str, &[u8])]) {
    let mut w = zip::ZipWriter::new(std::fs::File::create(path).unwrap());
    for (n, data) in files {
        w.start_file(*n, zip::write::SimpleFileOptions::default()).unwrap();
        w.write_all(data).unwrap();
    }
    w.finish().unwrap();
}

fn fixture() -> Fixture {
    let root = std::env::temp_dir().join(format!("kiln-publish-plan-{}", crate::util::uuid_v4().unwrap()));
    let project = root.join("project");
    std::fs::create_dir_all(project.join("docs")).unwrap();
    let zip = project.join("good-mod-1.2.3.zip");
    zip_of(&zip, &[("good-mod/kiln.mod.json", MANIFEST.as_bytes()), ("good-mod/GoodMod.dll", b"MZ made-up")]);
    std::fs::write(project.join("docs/notes.md"), "## 1.2.3\n- fixed the thing\n").unwrap();
    let config = project.join("kiln.publish.json");
    std::fs::write(&config, format!(r#"{{"format": 1, "provider": "github", "github": {{"repository": "example/good-mod", "tag": "v{{version}}", "commit": "{COMMIT}"}}, "notes": "docs/notes.md"}}"#)).unwrap();
    Fixture { data: root.join("data"), root, zip, config }
}

fn plan(f: &Fixture, detached: bool) -> Planned {
    create(&PlanRequest { data_root: &f.data, artifact: &f.zip, config: &f.config, provider: "github", detached }).unwrap()
}

fn overwrite(path: &Path, data: &[u8]) {
    let mut p = std::fs::metadata(path).unwrap().permissions();
    #[allow(clippy::permissions_set_readonly_false)]
    p.set_readonly(false);
    std::fs::set_permissions(path, p).unwrap();
    std::fs::write(path, data).unwrap();
}

/// `create` takes no transport and no credential: there is nothing it could send a request with. This test is the
/// record of that (the publish engine's network use starts at apply), and checks what a plan holds.
#[test]
fn a_plan_is_made_offline_and_names_only_frozen_files() {
    let f = fixture();
    let p = plan(&f, false);
    let plan = &p.plan;
    assert_eq!((plan.format, plan.provider.as_str(), plan.policy_version), (1, "github", policy::POLICY_VERSION));
    assert_eq!(plan.destination, Destination { repository: "example/good-mod".into(), tag: "v1.2.3".into(), prerelease: false });
    assert_eq!(plan.target_commit, COMMIT);
    assert_eq!(plan.mod_, PlannedMod { id: "good-mod".into(), version: "1.2.3".into() });
    let ops: Vec<(&str, &str)> = plan.operations.iter().map(|o| (o.kind.as_str(), o.id.as_str())).collect();
    assert_eq!(ops, [("create-draft", "draft"), ("upload-asset", "asset:good-mod-1.2.3.zip"), ("upload-asset", "asset:good-mod-1.2.3.zip.sha256"), ("upload-asset", "asset:kiln-release.json")]);
    assert!(plan.operations.iter().all(|o| o.kind != "publish-draft"), "publishing the draft is never part of a plan's apply");
    // the files are what the plan says
    let (sha, bytes) = crate::hash::sha256_file(&f.zip).unwrap();
    assert_eq!((plan.files.artifact.sha256.as_str(), plan.files.artifact.bytes), (sha.as_str(), bytes));
    assert_eq!(std::fs::read(p.dir.join(txdir::NOTES)).unwrap(), b"## 1.2.3\n- fixed the thing\n");
    let binding = ReleaseBinding::parse(&std::fs::read(p.dir.join(txdir::RELEASE)).unwrap()).unwrap();
    let rec = binding.record_for("good-mod").unwrap();
    assert_eq!((rec.artifact.name.as_str(), rec.artifact.sha256.as_str(), rec.artifact.bytes, rec.side), ("good-mod-1.2.3.zip", sha.as_str(), bytes, crate::manifest::Side::Client));
    assert_eq!(std::fs::read_to_string(p.dir.join(txdir::CHECKSUM)).unwrap(), format!("{sha}  good-mod-1.2.3.zip\n"));
    // no path of this PC and nothing of the notes' or config's location in the plan
    let text = std::fs::read_to_string(p.dir.join(txdir::PLAN)).unwrap();
    let home = std::env::var("HOME").unwrap_or_else(|_| "/home/".into());
    for needle in [f.root.to_str().unwrap(), std::env::temp_dir().to_str().unwrap(), home.as_str(), "docs/notes.md", "kiln.publish.json", "token", "ghp_"] {
        assert!(!text.contains(needle), "the plan holds {needle}");
    }
    assert!(text.contains(&plan.digest) && plan.digest == plan.compute_digest().unwrap());
    assert_eq!(PublishPlan::parse(text.as_bytes()).unwrap(), *plan);
    assert_eq!(body_marker(&plan.tx_id, &plan.digest), format!("<!-- kiln-publish:v1 tx={} plan={} -->", plan.tx_id, plan.digest));
    // the folder is the owner's alone and its files are read-only
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(std::fs::metadata(&p.dir).unwrap().permissions().mode() & 0o777, 0o700);
        for name in [txdir::ARTIFACT, txdir::CONFIG, txdir::NOTES, txdir::RELEASE, txdir::CHECKSUM, txdir::PLAN, txdir::ORIGIN] {
            let mode = std::fs::metadata(p.dir.join(name)).unwrap().permissions().mode() & 0o777;
            assert_eq!(mode & 0o222, 0, "{name} is writable ({mode:o})");
            assert_eq!(mode & 0o077, 0, "{name} is readable by others ({mode:o})");
        }
    }
    println!("PASS publish.no-network-plan");
}

#[test]
fn the_same_inputs_give_another_transaction_but_the_same_frozen_bytes() {
    let f = fixture();
    let (a, b) = (plan(&f, false), plan(&f, false));
    assert_ne!(a.plan.tx_id, b.plan.tx_id);
    assert_ne!(a.plan.digest, b.plan.digest);
    assert_eq!(a.plan.files, b.plan.files);
}

#[test]
fn a_loaded_transaction_is_verified_file_by_file() {
    let f = fixture();
    let p = plan(&f, false);
    assert_eq!(load(&f.data, &p.plan).unwrap().plan, p.plan);
    for name in [txdir::ARTIFACT, txdir::CONFIG, txdir::NOTES, txdir::RELEASE, txdir::CHECKSUM] {
        let path = p.dir.join(name);
        let before = std::fs::read(&path).unwrap();
        let mut changed = before.clone();
        changed.push(b' ');
        overwrite(&path, &changed);
        let e = load(&f.data, &p.plan).unwrap_err();
        assert_eq!(e.code, ErrorCode::HashMismatch, "{name}");
        assert!(e.message.contains(name));
        // same size, other bytes
        let mut flipped = before.clone();
        if let Some(b) = flipped.last_mut() {
            *b ^= 1;
            overwrite(&path, &flipped);
            assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::HashMismatch, "{name} flipped");
        }
        std::fs::remove_file(&path).unwrap();
        assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::HashMismatch, "{name} missing");
        overwrite_new(&path, &before);
        load(&f.data, &p.plan).unwrap();
    }
    // a link in place of a frozen file is not followed
    #[cfg(unix)]
    {
        let path = p.dir.join(txdir::NOTES);
        let copy = f.root.join("notes-copy");
        std::fs::copy(&path, &copy).unwrap();
        std::fs::remove_file(&path).unwrap();
        std::os::unix::fs::symlink(&copy, &path).unwrap();
        assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::HashMismatch);
    }
    println!("PASS publish.immutable-artifact");
}

fn overwrite_new(path: &Path, data: &[u8]) {
    std::fs::write(path, data).unwrap();
}

#[test]
fn a_changed_plan_or_project_config_refuses_and_detached_plans_do_not_need_the_project() {
    let f = fixture();
    let p = plan(&f, false);
    // an edited plan file: the digest no longer fits
    let text = p.plan.to_text().unwrap();
    for (from, to) in [("example/good-mod", "evil/good-mod"), ("v1.2.3", "v9.9.9"), (COMMIT, &"f".repeat(40)), ("artifact.zip", "../../x.zip"), ("create-draft", "publish-draft")] {
        let edited = text.replacen(from, to, 1);
        assert_ne!(edited, text);
        assert!(PublishPlan::parse(edited.as_bytes()).is_err(), "{from} -> {to}");
    }
    // an edited plan with a recomputed digest is a valid plan, but not this transaction's
    let mut forged = p.plan.clone();
    forged.destination.repository = "evil/good-mod".into();
    forged.digest = forged.compute_digest().unwrap();
    assert_eq!(load(&f.data, &forged).unwrap_err().code, ErrorCode::HashMismatch);
    // and one that names another file cannot be made at all
    let mut pointing = p.plan.clone();
    pointing.files.artifact.path = "origin.json".into();
    pointing.digest = pointing.compute_digest().unwrap();
    assert!(PublishPlan::parse(pointing.to_text().unwrap().as_bytes()).is_err());
    // unknown transaction
    let mut other = p.plan.clone();
    other.tx_id = crate::util::uuid_v4().unwrap();
    other.digest = other.compute_digest().unwrap();
    assert_eq!(load(&f.data, &other).unwrap_err().code, ErrorCode::NotFound);

    // the project's config changed after planning
    let detached = plan(&f, true);
    let original = std::fs::read(&f.config).unwrap();
    std::fs::write(&f.config, String::from_utf8(original.clone()).unwrap().replace("example/good-mod", "example/other")).unwrap();
    assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::Refused);
    load(&f.data, &detached.plan).unwrap();
    std::fs::remove_file(&f.config).unwrap();
    assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::Refused);
    load(&f.data, &detached.plan).unwrap();
    std::fs::write(&f.config, &original).unwrap();
    load(&f.data, &p.plan).unwrap();
    // the project's zip may change or go: only the frozen copy counts
    std::fs::remove_file(&f.zip).unwrap();
    load(&f.data, &p.plan).unwrap();
}

#[test]
fn a_refused_zip_leaves_no_transaction_behind() {
    let f = fixture();
    zip_of(&f.zip, &[("good-mod/kiln.mod.json", MANIFEST.as_bytes()), ("good-mod/.env", b"A=1")]);
    let e = create(&PlanRequest { data_root: &f.data, artifact: &f.zip, config: &f.config, provider: "github", detached: false }).unwrap_err();
    assert_eq!(policy::reason_of(&e), Some("secret-file"));
    assert_eq!(std::fs::read_dir(txdir::transactions(&f.data)).unwrap().count(), 0);
    // other refusals before anything is staged
    let e = create(&PlanRequest { data_root: &f.data, artifact: &f.zip, config: &f.config, provider: "nexus", detached: false }).unwrap_err();
    assert_eq!(e.code, ErrorCode::Unsupported);
    // a declared default config must match what is in the zip
    zip_of(&f.zip, &[("good-mod/kiln.mod.json", MANIFEST.as_bytes()), ("good-mod/GoodMod.dll", b"MZ"), ("good-mod/config/defaults/good-mod.json", b"{}")]);
    let e = create(&PlanRequest { data_root: &f.data, artifact: &f.zip, config: &f.config, provider: "github", detached: false }).unwrap_err();
    assert_eq!(policy::reason_of(&e), Some("undeclared-default-config"));
    assert_eq!(std::fs::read_dir(txdir::transactions(&f.data)).unwrap().count(), 0);
}

#[test]
fn a_zip_with_its_manifest_at_the_top_is_frozen_in_the_one_folder_layout() {
    let f = fixture();
    zip_of(&f.zip, &[("kiln.mod.json", MANIFEST.as_bytes()), ("GoodMod.dll", b"MZ made-up")]);
    let p = plan(&f, false);
    assert_ne!(p.plan.files.artifact.sha256, crate::hash::sha256_file(&f.zip).unwrap().0, "the frozen zip is the normalized one");
    assert_eq!(p.audit.members.iter().map(|m| m.name.as_str()).collect::<Vec<_>>(), ["good-mod/GoodMod.dll", "good-mod/kiln.mod.json"]);
    let binding = ReleaseBinding::parse(&std::fs::read(p.dir.join(txdir::RELEASE)).unwrap()).unwrap();
    assert_eq!(binding.mods[0].artifact.sha256, p.plan.files.artifact.sha256, "the binding names the bytes that are uploaded");
    load(&f.data, &p.plan).unwrap();
}

#[test]
fn transaction_ids_are_uuids_and_nothing_else_is_joined_to_the_folder() {
    assert!(txdir::is_tx_id(&crate::util::uuid_v4().unwrap()));
    for bad in ["", "..", "../../etc", "0123456789abcdef0123456789abcdef0123", "ABCDEF01-2345-6789-abcd-ef0123456789", "01234567-89ab-cdef-0123-456789abcde/"] {
        assert!(!txdir::is_tx_id(bad) && txdir::dir_of(Path::new("/data"), bad).is_err(), "{bad}");
    }
}

#[test]
fn an_old_audit_policy_needs_a_new_plan() {
    let f = fixture();
    let mut p = plan(&f, false).plan;
    p.policy_version = policy::POLICY_VERSION - 1;
    p.digest = p.compute_digest().unwrap();
    assert_eq!(PublishPlan::parse(p.to_text().unwrap().as_bytes()).unwrap_err().code, ErrorCode::Unsupported);
}

#[cfg(unix)]
#[test]
fn a_transaction_folder_link_or_a_staged_hardlink_is_refused() {
    let f = fixture();
    let p = plan(&f, false);
    let moved = f.root.join("moved-transaction");
    std::fs::rename(&p.dir, &moved).unwrap();
    std::os::unix::fs::symlink(&moved, &p.dir).unwrap();
    assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::HashMismatch);
    std::fs::remove_file(&p.dir).unwrap();
    std::fs::rename(&moved, &p.dir).unwrap();
    let linked = f.root.join("notes-hardlink");
    std::fs::hard_link(p.dir.join(txdir::NOTES), &linked).unwrap();
    assert_eq!(load(&f.data, &p.plan).unwrap_err().code, ErrorCode::HashMismatch);
    std::fs::remove_file(linked).unwrap();
    assert!(load(&f.data, &p.plan).is_ok());
}

#[test]
fn a_self_consistent_plan_cannot_add_publication_to_apply() {
    let f = fixture();
    let p = plan(&f, false);
    let mut edited = p.plan.clone();
    edited.operations.push(Operation { kind: "publish-draft".into(), id: "publish".into(), asset: None, file: None });
    edited.digest = edited.compute_digest().unwrap();
    overwrite(&p.dir.join(txdir::PLAN), edited.to_text().unwrap().as_bytes());
    assert!(load(&f.data, &edited).is_err(), "apply accepted publication as a planned operation");
}

#[cfg(unix)]
#[test]
fn release_notes_cannot_follow_a_link_outside_the_project() {
    let f = fixture();
    let outside = f.root.join("private-notes");
    std::fs::write(&outside, "private material outside the project").unwrap();
    let notes = f.config.parent().unwrap().join("docs/notes.md");
    std::fs::remove_file(&notes).unwrap();
    std::os::unix::fs::symlink(&outside, &notes).unwrap();
    let result = create(&PlanRequest { data_root: &f.data, artifact: &f.zip, config: &f.config, provider: "github", detached: false });
    assert!(result.is_err(), "notes from outside the project were frozen for publication");
    assert_eq!(std::fs::read_dir(txdir::transactions(&f.data)).unwrap().count(), 0);
}
