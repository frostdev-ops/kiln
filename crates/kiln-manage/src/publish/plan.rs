//! The publish plan: what `kiln publish --plan` writes and `--apply` carries out. Format 1.
//!
//! A plan names one transaction, one destination and the frozen files of the transaction folder by relative name,
//! SHA-256 and size. It holds no path of this PC, no secret and nothing that is not needed to check, before any
//! request is sent, that what will be uploaded is exactly what was reviewed. Its identity (`digest`) is the SHA-256
//! of its canonical JSON ([`super::canon`]) without the `digest` member.
//!
//! Making a plan sends nothing: it audits the zip, freezes it with the config and the notes, and writes files.

use super::artifact::{self, AuditInput, AuditReport};
use super::config::PublishConfig;
use super::{canon, policy, txdir};
use crate::release_binding::{BoundArtifact, BoundMod, ReleaseBinding};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const FORMAT: u32 = 1;
pub const MAX_NOTES_BYTES: u64 = 1024 * 1024;
/// The marker a draft release's body ends with: which transaction and plan made it.
pub fn body_marker(tx_id: &str, digest: &str) -> String {
    format!("<!-- kiln-publish:v1 tx={tx_id} plan={digest} -->")
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FrozenFile {
    pub path: String,
    pub sha256: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Files {
    pub artifact: FrozenFile,
    pub config: FrozenFile,
    pub notes: FrozenFile,
    pub release: FrozenFile,
    pub checksum: FrozenFile,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Destination {
    pub repository: String,
    pub tag: String,
    pub prerelease: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlannedMod {
    pub id: String,
    pub version: String,
}

/// One planned step, in order. `publish-draft` is never part of an apply: it is its own confirmed action.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Operation {
    /// `create-draft`, `upload-asset`, `publish-draft`.
    pub kind: String,
    /// The step's stable id inside the transaction: `draft`, `asset:<name>`, `publish`.
    pub id: String,
    /// For an upload: the asset's name at the provider and the frozen file it is read from.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub asset: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PublishPlan {
    pub format: u32,
    pub tx_id: String,
    pub policy_version: u32,
    pub provider: String,
    pub destination: Destination,
    pub target_commit: String,
    #[serde(rename = "mod")]
    pub mod_: PlannedMod,
    /// SHA-256 of the project's kiln.publish.json as it was when the plan was made. Its path stays on this PC
    /// (`origin.json`).
    pub original_config_sha256: String,
    /// True when the plan was made to be applied without the project at hand (`--detached-plan`).
    pub detached: bool,
    pub files: Files,
    pub operations: Vec<Operation>,
    /// SHA-256 of the canonical JSON of every other member.
    pub digest: String,
}

/// Where the inputs came from, kept beside the plan and never sent anywhere.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Origin {
    pub config_path: String,
    pub artifact_path: String,
}

impl PublishPlan {
    fn value_without_digest(&self) -> Result<serde_json::Value> {
        let mut v = serde_json::to_value(self)?;
        v.as_object_mut().map(|o| o.remove("digest"));
        Ok(v)
    }

    pub fn compute_digest(&self) -> Result<String> {
        canon::digest(&self.value_without_digest()?)
    }

    /// The plan file's text: pretty JSON, for a person to read. Its identity does not depend on this layout.
    pub fn to_text(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)? + "\n")
    }

    /// Reads a plan file and checks its shape and its digest. A plan that was edited does not load.
    pub fn parse(bytes: &[u8]) -> Result<PublishPlan> {
        if bytes.len() > 256 * 1024 {
            return Err(Error::new(ErrorCode::Schema, "The plan file is too large to be a publish plan."));
        }
        let p: PublishPlan = serde_json::from_slice(bytes).map_err(|e| Error::new(ErrorCode::Parse, format!("The plan file is not a publish plan ({e}).")))?;
        if p.format != FORMAT {
            return Err(Error::new(ErrorCode::Unsupported, format!("The plan has format {}; this Kiln reads format {FORMAT}.", p.format)));
        }
        if p.policy_version != policy::POLICY_VERSION {
            return Err(Error::new(ErrorCode::Unsupported, "This plan was audited under another artifact policy; make a new plan."));
        }
        if !txdir::is_tx_id(&p.tx_id) {
            return Err(Error::new(ErrorCode::Schema, "The plan's transaction id is not valid."));
        }
        let files = [&p.files.artifact, &p.files.config, &p.files.notes, &p.files.release, &p.files.checksum];
        let names = [txdir::ARTIFACT, txdir::CONFIG, txdir::NOTES, txdir::RELEASE, txdir::CHECKSUM];
        // Only the fixed relative names: a plan can never point the engine at another file.
        if files.iter().zip(names).any(|(f, n)| f.path != n || !crate::release_binding::is_sha256_hex(&f.sha256)) {
            return Err(Error::new(ErrorCode::Schema, "The plan names files it may not name."));
        }
        let asset = p.asset_name();
        let upload = |name: &str, file: &str| Operation { kind: "upload-asset".into(), id: format!("asset:{name}"), asset: Some(name.into()), file: Some(file.into()) };
        let allowed = vec![
            Operation { kind: "create-draft".into(), id: "draft".into(), asset: None, file: None },
            upload(&asset, txdir::ARTIFACT),
            upload(&format!("{asset}.sha256"), txdir::CHECKSUM),
            upload(crate::release_binding::FILE_NAME, txdir::RELEASE),
        ];
        if p.provider != "github" || !super::config::is_repository(&p.destination.repository) || !super::config::is_commit(&p.target_commit)
            || !crate::release_binding::is_asset_name(&asset) || p.operations != allowed {
            return Err(Error::new(ErrorCode::Schema, "The plan's destination or ordered draft operations are not valid. Publication is a separate action."));
        }
        if p.compute_digest()? != p.digest {
            return Err(Error::new(ErrorCode::HashMismatch, "The plan was changed after it was made. Make a new plan."));
        }
        Ok(p)
    }

    /// The asset names of the release, in upload order.
    pub fn asset_name(&self) -> String {
        format!("{}-{}.zip", self.mod_.id, self.mod_.version)
    }
}

/// What a plan step needs.
pub struct PlanRequest<'a> {
    pub data_root: &'a Path,
    /// The packed mod zip.
    pub artifact: &'a Path,
    /// The project's kiln.publish.json.
    pub config: &'a Path,
    pub provider: &'a str,
    pub detached: bool,
}

/// A made plan and where its transaction lives.
#[derive(Debug)]
pub struct Planned {
    pub plan: PublishPlan,
    pub dir: PathBuf,
    pub audit: AuditReport,
}

/// Audits and freezes the inputs and writes the plan. No network, no credential. On any refusal the transaction
/// folder is removed again and nothing is left behind.
pub fn create(req: &PlanRequest) -> Result<Planned> {
    if req.provider != "github" {
        return Err(Error::new(ErrorCode::Unsupported, format!("Publishing to {} is not part of this Kiln (github is).", req.provider)));
    }
    let config_bytes = std::fs::read(req.config).map_err(crate::util::io_at(req.config, "reading"))?;
    let config = PublishConfig::parse(&config_bytes)?;
    let tx_id = crate::util::uuid_v4()?;
    let dir = txdir::create(req.data_root, &tx_id)?;
    match fill(req, &config, &config_bytes, &tx_id, &dir) {
        Ok(p) => Ok(p),
        Err(e) => {
            txdir::discard(&dir);
            Err(e)
        }
    }
}

fn fill(req: &PlanRequest, config: &PublishConfig, config_bytes: &[u8], tx_id: &str, dir: &Path) -> Result<Planned> {
    // The zip: normalized into the one-folder layout when its manifest is at the top, else copied as it is.
    let staged = dir.join(txdir::ARTIFACT);
    let (zip_sha, zip_bytes) = if artifact::normalize_root(req.artifact, &staged)? {
        txdir::freeze_existing(dir, txdir::ARTIFACT)?
    } else {
        txdir::freeze_copy(dir, txdir::ARTIFACT, req.artifact)?
    };
    // From here on only the frozen copy is read.
    let audit = artifact::audit(&staged, &AuditInput { expect_id: None, expect_version: None, package_files: &config.package_files })?;
    if audit.zip_sha256 != zip_sha {
        return Err(Error::new(ErrorCode::HashMismatch, "The staged zip changed while it was audited."));
    }
    let notes: Vec<u8> = match &config.notes {
        None => Vec::new(),
        Some(rel) => {
            use std::io::Read;
            let base = req.config.parent().filter(|p| !p.as_os_str().is_empty()).unwrap_or(Path::new("."));
            let path = base.join(rel);
            let project = base.canonicalize().map_err(crate::util::io_at(base, "reading"))?;
            let source = path.canonicalize().map_err(crate::util::io_at(&path, "reading"))?;
            if !source.starts_with(&project) {
                return Err(Error::new(ErrorCode::Refused, "The release notes leave the project through a link. Nothing was sent."));
            }
            let file = std::fs::File::open(&source).map_err(crate::util::io_at(&path, "reading"))?;
            let meta = file.metadata().map_err(crate::util::io_at(&path, "reading"))?;
            if !meta.is_file() || meta.len() > MAX_NOTES_BYTES {
                return Err(Error::new(ErrorCode::Refused, "The release notes must be a regular file no larger than 1 MiB."));
            }
            let mut bytes = Vec::new();
            file.take(MAX_NOTES_BYTES + 1).read_to_end(&mut bytes).map_err(crate::util::io_at(&path, "reading"))?;
            if bytes.len() as u64 > MAX_NOTES_BYTES {
                return Err(Error::new(ErrorCode::Refused, "The release notes grew beyond 1 MiB while reading."));
            }
            let text = String::from_utf8(bytes).map_err(|_| Error::new(ErrorCode::Refused, "The release notes are not UTF-8."))?;
            text.strip_prefix('\u{feff}').unwrap_or(&text).as_bytes().to_vec()
        }
    };
    let mut scan = policy::PemScan::default();
    scan.feed(&notes);
    if scan.found {
        return Err(Error::new(ErrorCode::Refused, "The release notes hold a private key."));
    }
    let asset = format!("{}-{}.zip", audit.id, audit.version);
    if !crate::release_binding::is_asset_name(&asset) {
        return Err(Error::new(ErrorCode::Refused, format!("{asset} cannot be an asset name (the version has characters a file name may not have).")));
    }
    let tag = config.github.tag.replace("{version}", &audit.version);
    let binding = ReleaseBinding::single(BoundMod {
        id: audit.id.clone(),
        version: audit.version.clone(),
        side: audit.side,
        api_version: audit.api_version.clone(),
        artifact: BoundArtifact { name: asset.clone(), sha256: zip_sha.clone(), bytes: zip_bytes },
    })
    .to_json()?;
    let frozen = |name: &str, (sha256, bytes): (String, u64)| FrozenFile { path: name.to_string(), sha256, bytes };
    let files = Files {
        artifact: frozen(txdir::ARTIFACT, (zip_sha.clone(), zip_bytes)),
        config: frozen(txdir::CONFIG, txdir::freeze_bytes(dir, txdir::CONFIG, &canon::canonical(&config.effective())?)?),
        notes: frozen(txdir::NOTES, txdir::freeze_bytes(dir, txdir::NOTES, &notes)?),
        release: frozen(txdir::RELEASE, txdir::freeze_bytes(dir, txdir::RELEASE, binding.as_bytes())?),
        checksum: frozen(txdir::CHECKSUM, txdir::freeze_bytes(dir, txdir::CHECKSUM, format!("{zip_sha}  {asset}\n").as_bytes())?),
    };
    let upload = |asset: &str, file: &str| Operation { kind: "upload-asset".into(), id: format!("asset:{asset}"), asset: Some(asset.into()), file: Some(file.into()) };
    let mut plan = PublishPlan {
        format: FORMAT,
        tx_id: tx_id.to_string(),
        policy_version: audit.policy_version,
        provider: "github".into(),
        destination: Destination { repository: config.github.repository.clone(), tag, prerelease: config.github.prerelease },
        target_commit: config.github.commit.clone(),
        mod_: PlannedMod { id: audit.id.clone(), version: audit.version.clone() },
        original_config_sha256: crate::hash::sha256_hex(config_bytes),
        detached: req.detached,
        files,
        operations: vec![
            Operation { kind: "create-draft".into(), id: "draft".into(), asset: None, file: None },
            upload(&asset, txdir::ARTIFACT),
            upload(&format!("{asset}.sha256"), txdir::CHECKSUM),
            upload(crate::release_binding::FILE_NAME, txdir::RELEASE),
        ],
        digest: String::new(),
    };
    plan.digest = plan.compute_digest()?;
    // origin.json first, plan.json last: a folder with a plan is complete.
    let abs = |p: &Path| std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf()).display().to_string();
    let origin = Origin { config_path: abs(req.config), artifact_path: abs(req.artifact) };
    txdir::freeze_bytes(dir, txdir::ORIGIN, (serde_json::to_string_pretty(&origin)? + "\n").as_bytes())?;
    txdir::sync_dir(dir)?;
    txdir::freeze_bytes(dir, txdir::PLAN, plan.to_text()?.as_bytes())?;
    txdir::sync_dir(dir)?;
    Ok(Planned { plan, dir: dir.to_path_buf(), audit })
}

/// A transaction loaded for apply or resume: the plan and its folder, with every frozen file rehashed.
#[derive(Debug)]
pub struct Loaded {
    pub plan: PublishPlan,
    pub dir: PathBuf,
}

/// Loads the transaction a plan names and verifies it before anything is sent: the plan in the folder is the plan
/// given (same digest), every frozen file has its recorded hash and size, and, unless the plan is detached, the
/// project's kiln.publish.json is still there and unchanged.
pub fn load(data_root: &Path, given: &PublishPlan) -> Result<Loaded> {
    let dir = txdir::dir_of(data_root, &given.tx_id)?;
    if dir.exists() { txdir::check_directory(&dir)?; }
    let stored = std::fs::read(dir.join(txdir::PLAN)).map_err(|_| Error::not_found("This PC has no transaction for that plan (plans are applied where they were made)."))?;
    let plan = PublishPlan::parse(&stored)?;
    if plan.digest != given.digest || plan != *given {
        return Err(Error::new(ErrorCode::HashMismatch, "The plan file is not the plan of its transaction. Make a new plan."));
    }
    verify_files(&dir, &plan)?;
    if !plan.detached {
        let origin: Origin = crate::util::read_json(&dir.join(txdir::ORIGIN))?.ok_or_else(|| Error::new(ErrorCode::HashMismatch, "The transaction lost its origin record. Make a new plan."))?;
        let now = std::fs::read(&origin.config_path).map(|b| crate::hash::sha256_hex(&b)).ok();
        if now.as_deref() != Some(plan.original_config_sha256.as_str()) {
            return Err(Error::new(ErrorCode::Refused, format!("{} was changed or removed since the plan was made. Nothing was sent; make a new plan (or plan with --detached-plan to apply without the project).", super::config::FILE_NAME)));
        }
    }
    Ok(Loaded { plan, dir })
}

/// Rehashes every frozen file of a plan.
pub fn verify_files(dir: &Path, plan: &PublishPlan) -> Result<()> {
    for f in [&plan.files.artifact, &plan.files.config, &plan.files.notes, &plan.files.release, &plan.files.checksum] {
        txdir::open_verified(dir, &f.path, &f.sha256, Some(f.bytes))?;
    }
    Ok(())
}

#[cfg(test)]
#[path = "plan_tests.rs"]
mod tests;
