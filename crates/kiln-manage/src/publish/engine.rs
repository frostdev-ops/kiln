//! The publish engine: applies a plan step by step, with the intent of every step on disk before its request and its
//! result on disk before the next step.
//!
//! Per step:
//! - `confirmed`: nothing to do.
//! - `planned`: rehash every frozen file, write the intent (`intent_durable`, a new attempt id), flush,
//!   then send.
//! - `intent_durable`, `outcome_unknown` or `refused` found in the journal: the provider is asked what
//!   exists first. Ours: adopt it. Provably not there: attempt again. Something else, or not decidable:
//!   `manual_reconcile`. It is never sent again blindly.
//! - `manual_reconcile`: stop; only `--resolve-step` moves it.
//!
//! An attempt that ends without a usable answer is reconciled at once the same way, and attempted again at most once
//! per run when it is provably not there. Whatever stops the engine, what was confirmed stays confirmed and is said
//! so: a partial result is never reported as a clean failure or as rolled back.

use super::journal::{Journal, JournalStep, StepState, PUBLISH_STEP};
use super::plan::{self, Loaded, Operation};
use super::provider::{PublishProvider, Reconcile, RemoteIds, StepCtx, StepResult};
use super::txdir;
use crate::{Error, ErrorCode, Result};
use serde::Serialize;
use std::collections::BTreeMap;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum OutcomeState {
    /// Every planned step is confirmed: the draft release exists with its assets. Not public yet.
    DraftReady,
    /// The draft was made public.
    Published,
    /// Some steps are confirmed, one was refused or could not be sent; a resume goes on from there.
    Partial,
    /// A step needs a person to look at the provider.
    ManualReconcile,
    /// The first step was refused: nothing exists at the provider.
    Refused,
}

/// How an apply, a resume, a publish or a resolve ended. Serialized for `--json`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PublishOutcome {
    pub ok: bool,
    pub state: OutcomeState,
    pub tx_id: String,
    pub confirmed_steps: Vec<String>,
    pub unknown_steps: Vec<String>,
    pub remote_ids: BTreeMap<String, RemoteIds>,
    pub reason: Option<String>,
    /// What a person has to do, in words, when the engine cannot go on by itself.
    pub manual_steps: Vec<String>,
}

/// One durable state change, for a progress display. Holds no credential and no URL.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Progress {
    pub tx_id: String,
    pub step: String,
    pub state: StepState,
    pub reason: Option<String>,
}

/// Where a test can stop the engine as a crash would: after the intent is on disk and before the request, or after
/// the provider answered and before the answer is on disk. Production passes [`no_faults`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Boundary {
    BeforeSend,
    AfterSend,
}
pub type Fault<'a> = &'a dyn Fn(&str, Boundary) -> Result<()>;
pub fn no_faults(_: &str, _: Boundary) -> Result<()> {
    Ok(())
}

pub struct Engine<'a> {
    pub provider: &'a dyn PublishProvider,
    pub events: &'a mut dyn FnMut(&Progress),
    pub fault: Fault<'a>,
}

/// The exclusive lock of a transaction: one apply or resume at a time. Released when dropped.
pub struct TxLock(#[allow(dead_code)] std::fs::File);

/// How often [`lock`] tries a lock that is held, and how long it waits between tries (about a second in all).
const LOCK_TRIES: u32 = 20;
const LOCK_PAUSE: std::time::Duration = std::time::Duration::from_millis(50);

/// Takes the transaction's lock, or answers Busy when another holder keeps it for about a second.
///
/// Why it waits at all: the lock belongs to the open file, and a child process that another thread of this program
/// forks at that moment (a credential-store helper, a test's fake tool) holds an inherited copy of the descriptor
/// until it execs, even when it is opened close-on-exec. For that short time a lock this program already released
/// still counts as held. A real second `kiln publish` holds it for its whole run and is still answered Busy.
pub fn lock(dir: &std::path::Path) -> Result<TxLock> {
    lock_waiting(dir, LOCK_TRIES, LOCK_PAUSE)
}

fn lock_waiting(dir: &std::path::Path, tries: u32, pause: std::time::Duration) -> Result<TxLock> {
    txdir::check_directory(dir)?;
    let path = dir.join("lock");
    let f = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(&path).map_err(crate::util::io_at(&path, "locking"))?;
    for attempt in 1..=tries.max(1) {
        match f.try_lock() {
            Ok(()) => return Ok(TxLock(f)),
            Err(std::fs::TryLockError::WouldBlock) if attempt < tries => std::thread::sleep(pause),
            Err(_) => break,
        }
    }
    Err(Error::new(ErrorCode::Busy, "Another kiln publish is working on this transaction."))
}

fn known_ids(j: &Journal) -> BTreeMap<String, RemoteIds> {
    j.steps.iter().filter(|s| s.state == StepState::Confirmed).map(|s| (s.id.clone(), s.remote_ids.clone())).collect()
}

impl Engine<'_> {
    fn set(&mut self, j: &mut Journal, loaded: &Loaded, id: &str, f: impl FnOnce(&mut JournalStep)) -> Result<()> {
        let step = j.step_mut(id).ok_or_else(|| Error::new(ErrorCode::Schema, "the journal has no such step"))?;
        f(step);
        let (state, reason) = (step.state, step.reason.clone());
        let p = Progress { tx_id: j.tx_id.clone(), step: id.to_string(), state, reason };
        j.persist(&loaded.dir)?;
        (self.events)(&p);
        Ok(())
    }

    /// Carries one step to a durable end state. Returns that state.
    fn run_step(&mut self, j: &mut Journal, loaded: &Loaded, op: &Operation) -> Result<StepState> {
        let mut attempts_left = 2;
        loop {
            let state = j.step_mut(&op.id).map(|s| s.state).unwrap_or(StepState::Planned);
            let attempt = j.step_mut(&op.id).and_then(|s| s.attempt_id.clone()).unwrap_or_default();
            let known = known_ids(j);
            match state {
                StepState::Confirmed | StepState::ManualReconcile => return Ok(state),
                StepState::IntentDurable | StepState::OutcomeUnknown | StepState::Refused => {
                    // We do not know whether the earlier attempt arrived: ask before anything is sent again.
                    let ctx = StepCtx { plan: &loaded.plan, op, dir: &loaded.dir, known: &known, attempt: &attempt };
                    match self.provider.reconcile(&ctx) {
                        Reconcile::Adopt(ids) => self.set(j, loaded, &op.id, |s| {
                            s.state = StepState::Confirmed;
                            s.remote_ids = ids;
                            s.reason = None;
                        })?,
                        // Provably not there: the step is as if never sent. It is attempted again below while
                        // this run has attempts left; otherwise it stays planned for the next resume.
                        Reconcile::NotThere => self.set(j, loaded, &op.id, |s| {
                            s.state = StepState::Planned;
                            s.reason = Some("an earlier attempt did not arrive".into());
                        })?,
                        Reconcile::Conflict(why) | Reconcile::Ambiguous(why) => self.set(j, loaded, &op.id, |s| {
                            s.state = StepState::ManualReconcile;
                            s.reason = Some(why);
                        })?,
                    }
                }
                StepState::Planned => {
                    if attempts_left == 0 {
                        return Ok(state);
                    }
                    attempts_left -= 1;
                    // Nothing leaves this PC that is not byte for byte what was planned.
                    plan::verify_files(&loaded.dir, &loaded.plan)?;
                    let attempt = crate::util::uuid_v4()?;
                    self.set(j, loaded, &op.id, |s| {
                        s.state = StepState::IntentDurable;
                        s.attempt_id = Some(attempt.clone());
                        s.reason = None;
                    })?;
                    (self.fault)(&op.id, Boundary::BeforeSend)?;
                    let ctx = StepCtx { plan: &loaded.plan, op, dir: &loaded.dir, known: &known, attempt: &attempt };
                    let result = self.provider.execute(&ctx);
                    (self.fault)(&op.id, Boundary::AfterSend)?;
                    match result {
                        StepResult::Confirmed(ids) => self.set(j, loaded, &op.id, |s| {
                            s.state = StepState::Confirmed;
                            s.remote_ids = ids;
                        })?,
                        StepResult::Refused(why) | StepResult::NotSent(why) => {
                            self.set(j, loaded, &op.id, |s| {
                                s.state = StepState::Refused;
                                s.reason = Some(why);
                            })?;
                            return Ok(StepState::Refused);
                        }
                        StepResult::Unknown(why) => self.set(j, loaded, &op.id, |s| {
                            s.state = StepState::OutcomeUnknown;
                            s.reason = Some(why);
                        })?,
                    }
                }
            }
        }
    }

    fn outcome(&self, j: &Journal, loaded: &Loaded, publish: bool) -> PublishOutcome {
        let planned: Vec<&JournalStep> = j.steps.iter().filter(|s| s.id != PUBLISH_STEP).collect();
        let confirmed: Vec<String> = j.steps.iter().filter(|s| s.state == StepState::Confirmed).map(|s| s.id.clone()).collect();
        let unknown: Vec<String> = j.steps.iter().filter(|s| matches!(s.state, StepState::IntentDurable | StepState::OutcomeUnknown | StepState::ManualReconcile)).map(|s| s.id.clone()).collect();
        let stuck = j.steps.iter().find(|s| s.state != StepState::Confirmed && (publish || s.id != PUBLISH_STEP));
        let known = known_ids(j);
        let published = j.steps.iter().any(|s| s.id == PUBLISH_STEP && s.state == StepState::Confirmed);
        let all_planned = planned.iter().all(|s| s.state == StepState::Confirmed);
        let state = if published {
            OutcomeState::Published
        } else if !unknown.is_empty() {
            OutcomeState::ManualReconcile
        } else if all_planned && stuck.is_none() {
            OutcomeState::DraftReady
        } else if confirmed.is_empty() {
            OutcomeState::Refused
        } else {
            OutcomeState::Partial
        };
        let mut manual = Vec::new();
        for s in j.steps.iter().filter(|s| s.state == StepState::ManualReconcile) {
            let op = loaded.plan.operations.iter().find(|o| o.id == s.id).cloned().unwrap_or(Operation { kind: "publish-draft".into(), id: PUBLISH_STEP.into(), asset: None, file: None });
            let ctx = StepCtx { plan: &loaded.plan, op: &op, dir: &loaded.dir, known: &known, attempt: s.attempt_id.as_deref().unwrap_or("") };
            manual.push(format!("{}: {} Then: kiln publish --resolve-step {} --remote-id <id> --transaction {} --confirm", s.id, self.provider.lookup_hint(&ctx), s.id, j.tx_id));
        }
        let ok = matches!(state, OutcomeState::DraftReady | OutcomeState::Published) && (!publish || published);
        PublishOutcome { ok, state, tx_id: j.tx_id.clone(), confirmed_steps: confirmed, unknown_steps: unknown, remote_ids: known, reason: stuck.and_then(|s| s.reason.clone()), manual_steps: manual }
    }

    /// Applies or resumes a loaded transaction: every planned step, in order, stopping at the first that does not
    /// end confirmed. Publishing the draft is not part of it.
    pub fn apply(&mut self, loaded: &Loaded) -> Result<PublishOutcome> {
        let _lock = lock(&loaded.dir)?;
        let mut j = Journal::load_or_new(&loaded.dir, &loaded.plan)?;
        j.persist(&loaded.dir)?;
        for op in &loaded.plan.operations {
            if self.run_step(&mut j, loaded, op)? != StepState::Confirmed {
                break;
            }
        }
        Ok(self.outcome(&j, loaded, false))
    }

    /// Makes the draft public: its own intent, only when every planned step is confirmed.
    pub fn publish_draft(&mut self, loaded: &Loaded) -> Result<PublishOutcome> {
        let _lock = lock(&loaded.dir)?;
        let mut j = Journal::load_or_new(&loaded.dir, &loaded.plan)?;
        if j.steps.iter().filter(|s| s.id != PUBLISH_STEP).any(|s| s.state != StepState::Confirmed) {
            return Err(Error::new(ErrorCode::Refused, "The draft is not complete yet: finish the transaction first (kiln publish --resume)."));
        }
        if j.step_mut(PUBLISH_STEP).is_none() {
            let hash = super::journal::semantic_hash(&loaded.plan, PUBLISH_STEP)?;
            j.steps.push(JournalStep { id: PUBLISH_STEP.into(), semantic_hash: hash, state: StepState::Planned, attempt_id: None, remote_ids: RemoteIds::new(), reason: None });
            j.persist(&loaded.dir)?;
        }
        let op = Operation { kind: "publish-draft".into(), id: PUBLISH_STEP.into(), asset: None, file: None };
        self.run_step(&mut j, loaded, &op)?;
        Ok(self.outcome(&j, loaded, true))
    }

    /// Binds a step that needs manual reconciliation to what a person found at the provider. The id is checked by
    /// the provider (marker, destination, digest), not trusted.
    pub fn resolve_step(&mut self, loaded: &Loaded, step: &str, remote_id: &str) -> Result<PublishOutcome> {
        let _lock = lock(&loaded.dir)?;
        let mut j = Journal::load_or_new(&loaded.dir, &loaded.plan)?;
        let current = j.step_mut(step).map(|s| (s.state, s.attempt_id.clone().unwrap_or_default())).ok_or_else(|| Error::not_found("This transaction has no such step."))?;
        if current.0 != StepState::ManualReconcile {
            return Err(Error::new(ErrorCode::Refused, "That step does not wait for a manual reconciliation."));
        }
        let op = loaded.plan.operations.iter().find(|o| o.id == step).cloned().unwrap_or(Operation { kind: "publish-draft".into(), id: PUBLISH_STEP.into(), asset: None, file: None });
        let known = known_ids(&j);
        let ctx = StepCtx { plan: &loaded.plan, op: &op, dir: &loaded.dir, known: &known, attempt: &current.1 };
        match self.provider.verify(&ctx, remote_id) {
            Reconcile::Adopt(ids) => self.set(&mut j, loaded, step, |s| {
                s.state = StepState::Confirmed;
                s.remote_ids = ids;
                s.reason = None;
            })?,
            Reconcile::NotThere => return Err(Error::new(ErrorCode::Refused, "The provider has nothing under that id for this step. Nothing was changed.")),
            Reconcile::Conflict(why) | Reconcile::Ambiguous(why) => return Err(Error::new(ErrorCode::Refused, format!("That id is not this step's result: {why}. Nothing was changed."))),
        }
        Ok(self.outcome(&j, loaded, step == PUBLISH_STEP))
    }
}

/// Loads a transaction by its id alone (resume, publish-draft, resolve-step): the plan is the one in its folder.
pub fn load_by_id(data_root: &std::path::Path, tx_id: &str) -> Result<Loaded> {
    let dir = txdir::dir_of(data_root, tx_id)?;
    if dir.exists() { txdir::check_directory(&dir)?; }
    let stored = std::fs::read(dir.join(txdir::PLAN)).map_err(|_| Error::not_found("This PC has no such transaction."))?;
    let plan = plan::PublishPlan::parse(&stored)?;
    plan::load(data_root, &plan)
}

#[cfg(test)]
#[path = "engine_tests.rs"]
mod tests;
