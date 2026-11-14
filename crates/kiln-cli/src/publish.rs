// SPDX-License-Identifier: MIT
//! `kiln publish`: release a packed mod zip at GitHub, as a plan first and an explicit apply second.
//!
//! This file is the plan step: it audits the zip against the artifact policy, freezes it with the config and the
//! notes into a transaction folder under Kiln's data folder, and writes the plan for review. It sends nothing and
//! reads no credential.

use crate::out::{self, CliError};
use anyhow::Result;
use kiln_manage::publish::{config, plan, policy};
use serde_json::json;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(clap::Args)]
pub struct PublishArgs {
    /// A packed mod zip (<id>-<version>.zip from `kiln pack`) to plan a release for.
    #[arg(long, conflicts_with = "project")]
    pub artifact: Option<PathBuf>,
    /// A mod project: packed in Release first, then planned.
    #[arg(long)]
    pub project: Option<PathBuf>,
    /// The provider: github.
    #[arg(long)]
    pub to: Option<String>,
    /// Where to write the plan (plan step), a file to read and review before --apply.
    #[arg(long)]
    pub plan: Option<PathBuf>,
    /// The project's kiln.publish.json (default: in the project folder, or in the current folder with --artifact).
    #[arg(long)]
    pub config: Option<PathBuf>,
    /// Make a plan that can be applied without the project at hand (its kiln.publish.json is not checked again).
    #[arg(long)]
    pub detached_plan: bool,
    /// Carry out a plan made on this PC: creates the draft release and uploads the frozen files.
    #[arg(long, value_name = "PLAN")]
    pub apply: Option<PathBuf>,
    /// Continue a transaction that stopped (by its id).
    #[arg(long, value_name = "TRANSACTION")]
    pub resume: Option<String>,
    /// Make the draft release of a finished transaction public.
    #[arg(long, value_name = "TRANSACTION")]
    pub publish_draft: Option<String>,
    /// Bind a step that needs manual reconciliation to what exists at the provider (with --remote-id).
    #[arg(long, value_name = "STEP")]
    pub resolve_step: Option<String>,
    #[arg(long, value_name = "ID")]
    pub remote_id: Option<String>,
    /// Required by every action that sends something.
    #[arg(long)]
    pub confirm: bool,
}

/// Packs the project in Release into a folder of its own and returns the zip. Runs `kiln pack` as a child so that
/// the one packing path stays the only one.
fn pack_release(project: &Path, staging: &Path, json: bool) -> Result<PathBuf> {
    std::fs::create_dir_all(staging)?;
    let exe = std::env::current_exe()?;
    let out = Command::new(exe).arg("pack").arg(project).arg("-c").arg("Release").arg("-o").arg(staging).arg("--json").output()?;
    if !json {
        // the build's own output went to the child's stderr
        eprint!("{}", String::from_utf8_lossy(&out.stderr));
    }
    let v: serde_json::Value = serde_json::from_slice(&out.stdout).unwrap_or_default();
    match (v["ok"].as_bool(), v["zip"].as_str()) {
        (Some(true), Some(zip)) => Ok(PathBuf::from(zip)),
        _ => Err(CliError::new("pack_failed", "Packing the project in Release failed; nothing was planned.").with_detail(v).into()),
    }
}

fn plan_step(a: &PublishArgs, json: bool) -> Result<i32> {
    let to = a.to.as_deref().ok_or_else(|| CliError::usage("--to github is required"))?;
    let plan_out = a.plan.as_deref().ok_or_else(|| CliError::usage("--plan <file> says where to write the plan"))?;
    if plan_out.exists() {
        return Err(CliError::new("exists", format!("{} exists already; a plan is never overwritten.", plan_out.display())).into());
    }
    let dirs = kiln_manage::paths::DataDirs::locate()?;
    let staging = dirs.root.join("publish").join("staging").join(kiln_manage::util::uuid_v4()?);
    let result = (|| -> Result<plan::Planned> {
        let (artifact, base) = match (&a.artifact, &a.project) {
            (Some(zip), None) => (zip.clone(), std::env::current_dir()?),
            (None, Some(p)) => {
                let dir = if p.is_dir() { p.clone() } else { p.parent().unwrap_or(Path::new(".")).to_path_buf() };
                (pack_release(p, &staging, json)?, dir)
            }
            _ => return Err(CliError::usage("give --artifact <zip> or --project <project>").into()),
        };
        let config = a.config.clone().unwrap_or_else(|| base.join(config::FILE_NAME));
        if !config.is_file() {
            return Err(CliError::new("not_found", format!("{} is not there: it says where the mod is released (docs/kiln/modding/publishing.md).", config.display())).into());
        }
        Ok(plan::create(&plan::PlanRequest { data_root: &dirs.root, artifact: &artifact, config: &config, provider: to, detached: a.detached_plan })?)
    })();
    let _ = std::fs::remove_dir_all(&staging);
    let planned = result?;
    // The plan file for review is a copy of the transaction's own plan.json.
    kiln_manage::util::write_atomic_exclusive(plan_out, planned.plan.to_text()?.as_bytes())?;
    let p = &planned.plan;
    if json {
        out::print_json(&json!({"ok": true, "state": "planned", "txId": p.tx_id, "plan": plan_out.display().to_string(), "digest": p.digest,
            "policyVersion": policy::POLICY_VERSION, "mod": {"id": p.mod_.id, "version": p.mod_.version},
            "destination": {"repository": p.destination.repository, "tag": p.destination.tag, "commit": p.target_commit},
            "artifact": {"sha256": p.files.artifact.sha256, "bytes": p.files.artifact.bytes, "members": planned.audit.members.len()},
            "operations": p.operations.iter().map(|o| o.id.clone()).collect::<Vec<_>>()}));
    } else {
        println!("Planned: {} {} to github {} as a draft release at tag {} (commit {}).", p.mod_.id, p.mod_.version, p.destination.repository, p.destination.tag, &p.target_commit[..12]);
        println!("  zip      {} bytes, {} files, sha256 {}", p.files.artifact.bytes, planned.audit.members.len(), p.files.artifact.sha256);
        println!("  assets   {0}, {0}.sha256, kiln-release.json", p.asset_name());
        println!("  plan     {} (transaction {})", plan_out.display(), p.tx_id);
        println!("Nothing was sent. Read the plan, then: kiln publish --apply {} --confirm", plan_out.display());
    }
    Ok(0)
}

pub fn run(a: PublishArgs, json: bool) -> Result<i32> {
    let sending = a.apply.is_some() || a.resume.is_some() || a.publish_draft.is_some() || a.resolve_step.is_some();
    if sending {
        if a.artifact.is_some() || a.project.is_some() || a.plan.is_some() {
            return Err(CliError::usage("a plan step (--artifact/--project with --plan) and an action on a plan (--apply, --resume, --publish-draft, --resolve-step) are separate commands").into());
        }
        if !a.confirm {
            return Err(CliError::usage("this sends requests to the provider: add --confirm once you have read the plan").into());
        }
        return Err(CliError::new("not_implemented", "This build of kiln makes publish plans only; --apply, --resume, --publish-draft and --resolve-step are not in it yet.").into());
    }
    if a.confirm || a.remote_id.is_some() {
        return Err(CliError::usage("--confirm and --remote-id belong to --apply, --resume, --publish-draft or --resolve-step").into());
    }
    plan_step(&a, json)
}
