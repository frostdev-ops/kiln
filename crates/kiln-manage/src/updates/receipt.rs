//! Source receipts: what Kiln knows about where an installed mod's bytes came from, and which source the player
//! accepted for a mod. `<data>/updates/receipts.json` (schema/update-receipts.schema.json).
//!
//! - A [`SourceReceipt`] belongs to one library entry (the full [`LibraryRef`]: kind, id, version, content hash). It is
//!   written when Kiln itself saw the bytes arrive: a Nexus Mods install (file chain and version ids) and a library
//!   import of an archive (the archive's SHA-256). Fields nobody observed stay `null`; nothing is guessed from a file
//!   name or a homepage.
//! - One archive that holds several mods gives each of them a receipt with the same archive digest.
//! - A [`SourceRecord`] belongs to a mod (kind and id, across versions): the source the last check used and the
//!   player accepted, and, for a manual mapping, the mapping itself. A manual mapping is provenance only: it says
//!   where to look, not that the installed bytes came from there.
//! - Existing `nexus/installs.json` records become receipts ([`migrate_nexus_installs`]); that file stays the Nexus
//!   catalog's own and is not changed.

use super::status::{Provider, SourceOrigin};
use super::POLICY_VERSION;
use crate::library::LibraryRef;
use crate::manifest::{ModKind, UpdatesSpec, NEXUS_GAME_ID};
use crate::paths::DataDirs;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const FILE_NAME: &str = "receipts.json";
pub const FORMAT: u32 = 1;
/// Nexus Mods' name for the game: the `destination` of a Nexus receipt.
pub const NEXUS_DESTINATION: &str = "enshrouded";

/// Where a receipt's bytes came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ReceiptProvider {
    /// An archive the player imported by hand: its digest is known, its origin is not.
    Local,
    Github,
    Nexus,
}

/// What Kiln recorded when one library entry was installed or imported.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceReceipt {
    pub library_ref: LibraryRef,
    pub provider: ReceiptProvider,
    /// `owner/repo`, `enshrouded`, or empty for a hand import.
    pub destination: String,
    /// The mod's id at the provider (Nexus Mods: the page number); the library id for a hand import.
    pub mod_id: String,
    /// The version as the provider named it at install (Nexus versions are free text); the library version for a
    /// hand import.
    pub installed_version: String,
    pub release_id: Option<String>,
    pub tag: Option<String>,
    pub asset_id: Option<String>,
    /// SHA-256 of the archive the entry was unpacked from, lower-case hex.
    pub artifact_sha256: Option<String>,
    /// Nexus Mods: the global id of the file (the chain of its versions).
    pub file_chain_id: Option<String>,
    /// Nexus Mods: the global id of the installed version of that file.
    pub file_version_id: Option<String>,
    /// SHA-256 of the release's `kiln-release.json`, when the install read one.
    pub metadata_sha256: Option<String>,
    pub policy_version: u32,
}

impl SourceReceipt {
    fn local(r: &LibraryRef) -> SourceReceipt {
        SourceReceipt {
            library_ref: r.clone(),
            provider: ReceiptProvider::Local,
            destination: String::new(),
            mod_id: r.id.clone(),
            installed_version: r.version.clone(),
            release_id: None,
            tag: None,
            asset_id: None,
            artifact_sha256: None,
            file_chain_id: None,
            file_version_id: None,
            metadata_sha256: None,
            policy_version: POLICY_VERSION,
        }
    }

    /// The source this receipt proves, if it proves one: a Nexus receipt names its file chain. A hand import proves
    /// none, and a GitHub receipt alone does not say how the repository tags its releases.
    pub fn source(&self) -> Option<UpdatesSpec> {
        match self.provider {
            ReceiptProvider::Nexus => Some(UpdatesSpec::Nexus { game_id: NEXUS_GAME_ID, mod_id: self.mod_id.parse().ok()?, file_chain_id: self.file_chain_id.clone()? }),
            ReceiptProvider::Github | ReceiptProvider::Local => None,
        }
    }
}

/// Which provider and which place there: what "the same source" means. Tag pattern, asset name and channel are how
/// a source is read, not which source it is.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceIdentity {
    pub provider: Provider,
    /// GitHub: `owner/repo` in lower case (GitHub does not tell cases apart). Nexus Mods:
    /// `enshrouded/<mod id>/<file chain id>`.
    pub destination: String,
}

impl SourceIdentity {
    pub fn of(spec: &UpdatesSpec) -> SourceIdentity {
        match spec {
            UpdatesSpec::Github { repository, .. } => SourceIdentity { provider: Provider::Github, destination: repository.to_ascii_lowercase() },
            UpdatesSpec::Nexus { mod_id, file_chain_id, .. } => SourceIdentity { provider: Provider::Nexus, destination: format!("{NEXUS_DESTINATION}/{mod_id}/{file_chain_id}") },
        }
    }
}

/// The source accepted for a mod.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceRecord {
    pub kind: ModKind,
    pub id: String,
    #[serde(flatten)]
    pub identity: SourceIdentity,
    pub origin: SourceOrigin,
    /// A manual mapping's source. None for a source that comes from a receipt or the manifest (read from there).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub spec: Option<UpdatesSpec>,
    /// When it was accepted (RFC 3339, UTC).
    pub accepted: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReceiptFile {
    pub format: u32,
    pub policy_version: u32,
    #[serde(default)]
    pub receipts: Vec<SourceReceipt>,
    #[serde(default)]
    pub sources: Vec<SourceRecord>,
}

impl ReceiptFile {
    pub fn receipt(&self, r: &LibraryRef) -> Option<&SourceReceipt> {
        self.receipts.iter().find(|x| &x.library_ref == r)
    }

    fn receipt_mut(&mut self, r: &LibraryRef) -> &mut SourceReceipt {
        let at = match self.receipts.iter().position(|x| &x.library_ref == r) {
            Some(i) => i,
            None => {
                self.receipts.push(SourceReceipt::local(r));
                self.receipts.len() - 1
            }
        };
        &mut self.receipts[at]
    }

    pub fn source(&self, kind: ModKind, id: &str) -> Option<&SourceRecord> {
        self.sources.iter().find(|s| s.kind == kind && s.id == id)
    }

    /// A manual mapping, if the player made one for the mod.
    pub fn manual(&self, kind: ModKind, id: &str) -> Option<&UpdatesSpec> {
        self.source(kind, id).filter(|s| s.origin == SourceOrigin::Manual).and_then(|s| s.spec.as_ref())
    }

    fn put_source(&mut self, rec: SourceRecord) {
        self.sources.retain(|s| !(s.kind == rec.kind && s.id == rec.id));
        self.sources.push(rec);
        self.sources.sort_by(|a, b| (a.kind, &a.id).cmp(&(b.kind, &b.id)));
    }

    /// Accepts `spec` as the mod's source (what a first check does by itself, and what "confirm" does after a change).
    pub fn accept(&mut self, kind: ModKind, id: &str, spec: &UpdatesSpec, origin: SourceOrigin, now: i64) {
        let spec_kept = (origin == SourceOrigin::Manual).then(|| spec.clone());
        self.put_source(SourceRecord { kind, id: id.to_string(), identity: SourceIdentity::of(spec), origin, spec: spec_kept, accepted: crate::util::rfc3339(now) });
    }

    /// Records that these library entries were unpacked from the archive with this digest. An entry that already has
    /// a receipt keeps what it says about its provider and gets the digest of the archive seen last.
    pub fn bind_archive(&mut self, refs: &[LibraryRef], sha256: &str) {
        for r in refs {
            self.receipt_mut(r).artifact_sha256 = Some(sha256.to_string());
        }
    }

    /// Records a Nexus Mods install for the library entries it produced. Returns whether anything changed.
    pub fn bind_nexus(&mut self, rec: &crate::nexus::catalog::InstallRecord) -> bool {
        let mut changed = false;
        for r in &rec.refs {
            let before = self.receipt(r).cloned();
            let x = self.receipt_mut(r);
            x.provider = ReceiptProvider::Nexus;
            x.destination = NEXUS_DESTINATION.to_string();
            x.mod_id = rec.mod_id.clone();
            x.installed_version = rec.version.clone();
            x.file_chain_id = Some(rec.file_id.clone()).filter(|s| !s.is_empty());
            x.file_version_id = Some(rec.version_id.clone()).filter(|s| !s.is_empty());
            // fields of another provider do not describe this install
            x.release_id = None;
            x.tag = None;
            x.asset_id = None;
            x.metadata_sha256 = None;
            changed |= before.as_ref() != Some(&*x);
        }
        changed
    }
}

/// `<data>/updates/receipts.json`
pub fn path_of(dirs: &DataDirs) -> PathBuf {
    super::root_of(dirs).join(FILE_NAME)
}

/// Reads the file. Missing: an empty one. Written by a newer Kiln: an error (it is not rewritten by this one). Not
/// readable as JSON: it is set aside as `receipts.json.unreadable` and an empty one is used; what is lost is
/// provenance, so the mods it described read as unbound, which is the safe side.
pub fn load(dirs: &DataDirs) -> Result<ReceiptFile> {
    let path = path_of(dirs);
    match crate::util::read_json::<ReceiptFile>(&path) {
        Ok(None) => Ok(ReceiptFile { format: FORMAT, policy_version: POLICY_VERSION, ..Default::default() }),
        Ok(Some(f)) if f.format == FORMAT => Ok(f),
        Ok(Some(f)) => Err(Error::new(ErrorCode::Unsupported, format!("{}: format {} was written by another Kiln version (this one reads {FORMAT}); it is left as it is", path.display(), f.format))),
        Err(e) if e.code == ErrorCode::Parse => {
            let aside = crate::util::with_suffix(&path, ".unreadable");
            let _ = std::fs::rename(&path, &aside);
            Ok(ReceiptFile { format: FORMAT, policy_version: POLICY_VERSION, ..Default::default() })
        }
        Err(e) => Err(e),
    }
}

/// One read-modify-write of the file, serialized across threads and processes. `f` returns whether it changed
/// anything; the file is written only then.
pub fn update<T>(dirs: &DataDirs, f: impl FnOnce(&mut ReceiptFile) -> (bool, T)) -> Result<T> {
    let _lock = super::lock(dirs)?;
    let mut file = load(dirs)?;
    let (changed, out) = f(&mut file);
    if changed {
        file.format = FORMAT;
        file.policy_version = POLICY_VERSION;
        file.receipts.sort_by(|a, b| key(&a.library_ref).cmp(&key(&b.library_ref)));
        crate::util::write_json_atomic(&path_of(dirs), &file)?;
    }
    Ok(out)
}

fn key(r: &LibraryRef) -> (ModKind, &str, &str, &str) {
    (r.kind, r.id.as_str(), r.version.as_str(), r.hash.as_str())
}

/// Library import: these entries came out of the archive with this SHA-256.
pub fn capture_archive(dirs: &DataDirs, refs: &[LibraryRef], sha256: &str) -> Result<()> {
    if refs.is_empty() || !crate::release_binding::is_sha256_hex(sha256) {
        return Ok(());
    }
    update(dirs, |f| {
        let before = f.receipts.clone();
        f.bind_archive(refs, sha256);
        (f.receipts != before, ())
    })
}

/// Nexus Mods install: the record the catalog just wrote, as receipts.
pub fn capture_nexus(dirs: &DataDirs, rec: &crate::nexus::catalog::InstallRecord) -> Result<()> {
    update(dirs, |f| (f.bind_nexus(rec), ()))
}

#[derive(Deserialize)]
struct NexusInstalls {
    #[serde(default)]
    mods: Vec<crate::nexus::catalog::InstallRecord>,
}

/// Turns the records of `nexus/installs.json` (installs made before receipts existed) into receipts. Reads that
/// file and never writes it. A record already present as a receipt changes nothing, so this runs before every
/// check. Returns how many records it read.
pub fn migrate_nexus_installs(dirs: &DataDirs) -> Result<usize> {
    let path = crate::nexus::catalog::root_of(dirs).join("installs.json");
    let Some(installs) = crate::util::read_json::<NexusInstalls>(&path)? else { return Ok(0) };
    let n = installs.mods.len();
    if n == 0 {
        return Ok(0);
    }
    update(dirs, |f| {
        let mut changed = false;
        for rec in &installs.mods {
            changed |= f.bind_nexus(rec);
        }
        (changed, n)
    })
}

/// A manual mapping: the player says where a mod's updates are. When the mod already has an accepted source at
/// another place (another provider, repository or file), the mapping is refused with `Conflict` unless `confirm`.
pub fn set_manual_source(dirs: &DataDirs, kind: ModKind, id: &str, spec: &UpdatesSpec, confirm: bool, now: i64) -> Result<()> {
    update(dirs, |f| {
        let new = SourceIdentity::of(spec);
        if let Some(old) = f.source(kind, id)
            && old.identity != new
            && !confirm
        {
            return (false, Err(Error::new(ErrorCode::Conflict, format!(
                "{id} is checked against {} ({}). Confirm the change to {} ({}) to use the new source.",
                old.identity.destination, old.identity.provider.as_str(), new.destination, new.provider.as_str()))));
        }
        f.accept(kind, id, spec, SourceOrigin::Manual, now);
        (true, Ok(()))
    })?
}

/// Removes a manual mapping (the next check goes back to the receipt or the manifest, and asks again if that is
/// another source than the mapping was).
pub fn clear_manual_source(dirs: &DataDirs, kind: ModKind, id: &str) -> Result<bool> {
    update(dirs, |f| {
        let Some(at) = f.sources.iter().position(|s| s.kind == kind && s.id == id && s.origin == SourceOrigin::Manual) else { return (false, false) };
        f.sources[at].origin = SourceOrigin::Manifest;
        f.sources[at].spec = None;
        (true, true)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(tag: &str) -> (DataDirs, PathBuf) {
        let d = std::env::temp_dir().join(format!("kiln-updates-receipt-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        (DataDirs::at(d.clone()), d)
    }

    fn lref(id: &str, version: &str) -> LibraryRef {
        LibraryRef { kind: ModKind::Kiln, id: id.into(), version: version.into(), hash: format!("kdh1:{id}{version}") }
    }

    fn github(repo: &str) -> UpdatesSpec {
        UpdatesSpec::Github { repository: repo.into(), tag_pattern: "v{version}".into(), asset: None, channel: Default::default() }
    }

    const SHA_A: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const SHA_B: &str = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    #[test]
    fn one_archive_gives_every_entry_it_produced_the_same_artifact_identity() {
        let (dirs, root) = tmp("archive");
        let refs = [lref("a", "1.0.0"), lref("b", "2.0.0"), LibraryRef { kind: ModKind::Native, ..lref("c", "0.1.0") }];
        capture_archive(&dirs, &refs, SHA_A).unwrap();
        let f = load(&dirs).unwrap();
        assert_eq!(f.receipts.len(), 3);
        for r in &refs {
            let x = f.receipt(r).unwrap();
            assert_eq!((x.provider, x.artifact_sha256.as_deref(), x.policy_version), (ReceiptProvider::Local, Some(SHA_A), POLICY_VERSION));
            assert_eq!((x.release_id.as_ref(), x.asset_id.as_ref(), x.file_chain_id.as_ref()), (None, None, None), "nothing observed, nothing recorded");
            assert!(x.source().is_none(), "a hand import proves no source");
        }
        // the same entry again from another archive: the archive seen last; other entries untouched
        capture_archive(&dirs, &refs[..1], SHA_B).unwrap();
        let f = load(&dirs).unwrap();
        assert_eq!(f.receipt(&refs[0]).unwrap().artifact_sha256.as_deref(), Some(SHA_B));
        assert_eq!(f.receipt(&refs[1]).unwrap().artifact_sha256.as_deref(), Some(SHA_A));
        assert_eq!(f.receipts.len(), 3);
        // something that is not a digest is not recorded
        capture_archive(&dirs, &[lref("d", "1.0.0")], "ABC").unwrap();
        assert_eq!(load(&dirs).unwrap().receipts.len(), 3);
        let _ = std::fs::remove_dir_all(root);
    }

    fn install_record(mod_id: &str, file: &str, version_id: &str, version: &str, refs: Vec<LibraryRef>) -> serde_json::Value {
        serde_json::json!({"modId": mod_id, "name": format!("Mod {mod_id}"), "fileId": file, "fileName": "Main file", "versionId": version_id,
            "versionGsid": "1001", "version": version, "refs": refs, "installed": "2026-11-07T10:00:00Z", "source": "nxm"})
    }

    #[test]
    fn nexus_install_records_migrate_to_receipts_and_the_file_they_come_from_is_not_touched() {
        let (dirs, root) = tmp("migrate");
        let nexus = root.join("nexus");
        std::fs::create_dir_all(&nexus).unwrap();
        let (a, b, c) = (lref("lantern-glow", "1.2.0"), lref("lantern-extras", "0.3.0"), lref("night-sky", "2.0.0"));
        let installs = serde_json::json!({"format": 1, "mods": [
            install_record("310", "90310", "8010012", "1.2.0", vec![a.clone(), b.clone()]),
            install_record("313", "90313", "", "v2 final", vec![c.clone()]),
        ]});
        let text = serde_json::to_string_pretty(&installs).unwrap();
        std::fs::write(nexus.join("installs.json"), &text).unwrap();
        // the first of them was imported from an archive before: its digest stays
        capture_archive(&dirs, std::slice::from_ref(&a), SHA_A).unwrap();

        assert_eq!(migrate_nexus_installs(&dirs).unwrap(), 2);
        let f = load(&dirs).unwrap();
        assert_eq!(f.receipts.len(), 3);
        let ra = f.receipt(&a).unwrap();
        assert_eq!((ra.provider, ra.destination.as_str(), ra.mod_id.as_str(), ra.installed_version.as_str()), (ReceiptProvider::Nexus, "enshrouded", "310", "1.2.0"));
        assert_eq!((ra.file_chain_id.as_deref(), ra.file_version_id.as_deref(), ra.artifact_sha256.as_deref()), (Some("90310"), Some("8010012"), Some(SHA_A)));
        assert_eq!(ra.source(), Some(UpdatesSpec::Nexus { game_id: 5811, mod_id: 310, file_chain_id: "90310".into() }));
        // one install record with two entries: both carry the same file chain
        assert_eq!(f.receipt(&b).unwrap().file_chain_id.as_deref(), Some("90310"));
        // a field the old record does not have stays null, and a free-text version is kept as it is
        let rc = f.receipt(&c).unwrap();
        assert_eq!((rc.file_version_id.as_deref(), rc.installed_version.as_str(), rc.artifact_sha256.as_deref()), (None, "v2 final", None));
        assert_eq!(std::fs::read_to_string(nexus.join("installs.json")).unwrap(), text, "installs.json is the catalog's file");

        // again: nothing changes, the file is not rewritten
        let before = std::fs::metadata(path_of(&dirs)).unwrap().modified().unwrap();
        let bytes = std::fs::read(path_of(&dirs)).unwrap();
        assert_eq!(migrate_nexus_installs(&dirs).unwrap(), 2);
        assert_eq!(std::fs::read(path_of(&dirs)).unwrap(), bytes);
        assert_eq!(std::fs::metadata(path_of(&dirs)).unwrap().modified().unwrap(), before);
        // no installs.json at all: nothing to do
        std::fs::remove_file(nexus.join("installs.json")).unwrap();
        assert_eq!(migrate_nexus_installs(&dirs).unwrap(), 0);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn a_manual_mapping_to_another_source_needs_confirmation() {
        let (dirs, root) = tmp("manual");
        let now = 1_800_000_000;
        set_manual_source(&dirs, ModKind::Emf, "better-loot", &github("Example/Better-Loot"), false, now).unwrap();
        let f = load(&dirs).unwrap();
        let s = f.source(ModKind::Emf, "better-loot").unwrap();
        assert_eq!((s.origin, s.identity.destination.as_str()), (SourceOrigin::Manual, "example/better-loot"));
        assert_eq!(f.manual(ModKind::Emf, "better-loot"), Some(&github("Example/Better-Loot")));
        // the same place spelled in another case, or read with another tag pattern, is the same source
        set_manual_source(&dirs, ModKind::Emf, "better-loot", &UpdatesSpec::Github {
            repository: "example/better-loot".into(), tag_pattern: "release-{version}".into(), asset: None, channel: Default::default() }, false, now).unwrap();
        // another repository: refused until confirmed, and nothing changed meanwhile
        let e = set_manual_source(&dirs, ModKind::Emf, "better-loot", &github("someone-else/better-loot"), false, now).unwrap_err();
        assert_eq!(e.code, ErrorCode::Conflict);
        assert_eq!(load(&dirs).unwrap().source(ModKind::Emf, "better-loot").unwrap().identity.destination, "example/better-loot");
        set_manual_source(&dirs, ModKind::Emf, "better-loot", &github("someone-else/better-loot"), true, now).unwrap();
        assert_eq!(load(&dirs).unwrap().source(ModKind::Emf, "better-loot").unwrap().identity.destination, "someone-else/better-loot");
        // another mod of the same id but another kind is another mod
        assert!(load(&dirs).unwrap().source(ModKind::Kiln, "better-loot").is_none());
        // clearing keeps the accepted place, so going back to another source asks again
        assert!(clear_manual_source(&dirs, ModKind::Emf, "better-loot").unwrap());
        let f = load(&dirs).unwrap();
        assert!(f.manual(ModKind::Emf, "better-loot").is_none());
        assert_eq!(f.source(ModKind::Emf, "better-loot").unwrap().identity.destination, "someone-else/better-loot");
        assert!(!clear_manual_source(&dirs, ModKind::Emf, "better-loot").unwrap());
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn a_damaged_file_is_set_aside_and_a_newer_format_is_left_alone() {
        let (dirs, root) = tmp("damaged");
        std::fs::create_dir_all(super::super::root_of(&dirs)).unwrap();
        std::fs::write(path_of(&dirs), b"{ not json").unwrap();
        assert!(load(&dirs).unwrap().receipts.is_empty());
        assert!(crate::util::with_suffix(&path_of(&dirs), ".unreadable").exists());
        capture_archive(&dirs, &[lref("a", "1.0.0")], SHA_A).unwrap();
        assert_eq!(load(&dirs).unwrap().receipts.len(), 1);
        let newer = r#"{"format": 2, "policyVersion": 9, "receipts": [], "future": true}"#;
        std::fs::write(path_of(&dirs), newer).unwrap();
        assert_eq!(load(&dirs).unwrap_err().code, ErrorCode::Unsupported);
        assert_eq!(capture_archive(&dirs, &[lref("b", "1.0.0")], SHA_A).unwrap_err().code, ErrorCode::Unsupported);
        assert_eq!(std::fs::read_to_string(path_of(&dirs)).unwrap(), newer);
        let _ = std::fs::remove_dir_all(root);
    }

    /// The schema's example is a file this code reads, and the schema lists the fields this code writes.
    #[test]
    fn the_schema_example_reads_and_the_schema_lists_the_fields() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema");
        let example: ReceiptFile = serde_json::from_slice(&std::fs::read(root.join("examples/update-receipts.json")).unwrap()).unwrap();
        assert_eq!((example.format, example.receipts.len(), example.sources.len()), (1, 3, 2));
        assert_eq!(example.receipts[0].source().map(|s| SourceIdentity::of(&s).destination).as_deref(), Some("enshrouded/310/90310"));
        let schema: serde_json::Value = serde_json::from_slice(&std::fs::read(root.join("update-receipts.schema.json")).unwrap()).unwrap();
        let keys = |v: &serde_json::Value| -> Vec<String> {
            let mut k: Vec<String> = v["properties"].as_object().unwrap().keys().cloned().collect();
            k.sort();
            k
        };
        let written = |v: &serde_json::Value| -> Vec<String> {
            let mut k: Vec<String> = v.as_object().unwrap().keys().cloned().collect();
            k.sort();
            k
        };
        let mut f = ReceiptFile { format: FORMAT, policy_version: POLICY_VERSION, ..Default::default() };
        f.bind_archive(&[lref("a", "1.0.0")], SHA_A);
        f.accept(ModKind::Kiln, "a", &github("example/a"), SourceOrigin::Manual, 1_800_000_000);
        let json = serde_json::to_value(&f).unwrap();
        assert_eq!(keys(&schema), written(&json));
        assert_eq!(keys(&schema["$defs"]["receipt"]), written(&json["receipts"][0]));
        assert_eq!(keys(&schema["$defs"]["source"]), written(&json["sources"][0]));
        assert_eq!(schema["$defs"]["receipt"]["additionalProperties"], false);
    }
}
