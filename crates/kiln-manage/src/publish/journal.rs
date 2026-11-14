//! The journal of a publish transaction (`journal.json`): one record per planned step with its state, written and
//! flushed before and after every request. It is what makes apply resumable: an intent is on disk before its request
//! is sent, so after a crash or a lost answer the engine knows which step to ask the provider about instead of
//! sending it again blindly.
//!
//! The journal holds ids and reasons only: no credential, no URL with a query, no response body.

use super::plan::PublishPlan;
use super::provider::RemoteIds;
use super::{canon, txdir};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StepState {
    Planned,
    /// The intent is on disk; the request may or may not have been sent.
    IntentDurable,
    /// The request was sent and no usable answer came back.
    OutcomeUnknown,
    Confirmed,
    /// The provider said no; nothing was made. A resume tries again.
    Refused,
    /// A person has to look at the provider; the engine does nothing more with this step until `--resolve-step`.
    ManualReconcile,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JournalStep {
    pub id: String,
    /// SHA-256 over what the step means (plan digest, operation, the frozen file's hash): a journal cannot be
    /// replayed against another plan.
    pub semantic_hash: String,
    pub state: StepState,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub attempt_id: Option<String>,
    #[serde(default, skip_serializing_if = "RemoteIds::is_empty")]
    pub remote_ids: RemoteIds,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Journal {
    pub format: u32,
    pub tx_id: String,
    pub plan_digest: String,
    pub steps: Vec<JournalStep>,
}

/// The step id of the separate "make the draft public" action.
pub const PUBLISH_STEP: &str = "publish";

pub fn semantic_hash(plan: &PublishPlan, step_id: &str) -> Result<String> {
    let op = plan.operations.iter().find(|o| o.id == step_id);
    let file = op.and_then(|o| o.file.as_deref());
    let file_sha = [&plan.files.artifact, &plan.files.release, &plan.files.checksum, &plan.files.notes, &plan.files.config]
        .into_iter()
        .find(|f| Some(f.path.as_str()) == file)
        .map(|f| f.sha256.clone());
    canon::digest(&serde_json::json!({"plan": plan.digest, "step": step_id, "kind": op.map(|o| o.kind.as_str()).unwrap_or("publish-draft"),
        "asset": op.and_then(|o| o.asset.clone()), "fileSha256": file_sha}))
}

impl Journal {
    pub fn new(plan: &PublishPlan) -> Result<Journal> {
        let mut steps = Vec::new();
        for op in &plan.operations {
            steps.push(JournalStep { id: op.id.clone(), semantic_hash: semantic_hash(plan, &op.id)?, state: StepState::Planned, attempt_id: None, remote_ids: RemoteIds::new(), reason: None });
        }
        Ok(Journal { format: 1, tx_id: plan.tx_id.clone(), plan_digest: plan.digest.clone(), steps })
    }

    /// Reads the journal of a transaction, or makes a new one. A journal of another plan, or one whose steps do not
    /// mean what the plan's steps mean, is refused.
    pub fn load_or_new(dir: &Path, plan: &PublishPlan) -> Result<Journal> {
        let path = dir.join(txdir::JOURNAL);
        let Some(j) = crate::util::read_json::<Journal>(&path)? else { return Journal::new(plan) };
        let wrong = || Error::new(ErrorCode::HashMismatch, "The transaction's journal does not belong to its plan. Nothing was sent.");
        if j.format != 1 || j.tx_id != plan.tx_id || j.plan_digest != plan.digest {
            return Err(wrong());
        }
        for (i, op) in plan.operations.iter().enumerate() {
            let ok = j.steps.get(i).is_some_and(|s| s.id == op.id && semantic_hash(plan, &op.id).is_ok_and(|h| h == s.semantic_hash));
            if !ok {
                return Err(wrong());
            }
        }
        // after the plan's steps only the publish step may follow
        if j.steps.len() > plan.operations.len() + 1 || j.steps.get(plan.operations.len()).is_some_and(|s| s.id != PUBLISH_STEP || semantic_hash(plan, PUBLISH_STEP).is_ok_and(|h| h != s.semantic_hash)) {
            return Err(wrong());
        }
        Ok(j)
    }

    /// Writes the journal and flushes it and its folder. Called before every request and after every answer.
    pub fn persist(&self, dir: &Path) -> Result<()> {
        let path = dir.join(txdir::JOURNAL);
        crate::util::write_atomic(&path, (serde_json::to_string_pretty(self)? + "\n").as_bytes())?;
        txdir::sync_dir(dir)
    }

    pub fn step_mut(&mut self, id: &str) -> Option<&mut JournalStep> {
        self.steps.iter_mut().find(|s| s.id == id)
    }
}
