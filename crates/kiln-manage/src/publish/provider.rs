//! What a release provider does for the publish engine: carry out one planned step, and say what exists at the
//! provider when the engine does not know whether an earlier attempt of that step arrived.
//!
//! The engine owns durability and order; a provider owns the requests. A provider never retries a mutation by
//! itself and never deletes or overwrites anything.

use super::plan::{Operation, PublishPlan};
use std::collections::BTreeMap;
use std::path::Path;

/// Ids the provider gave for what a step made (`release`, `asset`, `uploadUrl`-free: ids only, never a URL with a
/// signature and never a credential). They are journaled.
pub type RemoteIds = BTreeMap<String, String>;

pub struct StepCtx<'a> {
    pub plan: &'a PublishPlan,
    pub op: &'a Operation,
    /// The transaction folder: frozen files are opened from here with [`super::txdir::open_verified`].
    pub dir: &'a Path,
    /// Ids confirmed by earlier steps of this transaction, by step id (`draft` -> {`release`: ...}).
    pub known: &'a BTreeMap<String, RemoteIds>,
    /// The attempt's UUID, journaled before the request: a provider may put it where a later reconcile can find it.
    pub attempt: &'a str,
}

/// How an attempt ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StepResult {
    /// The provider confirmed it.
    Confirmed(RemoteIds),
    /// The provider answered no (401, 403, a rate limit, a validation error): nothing was made.
    Refused(String),
    /// The request did not leave (no connection): nothing was made.
    NotSent(String),
    /// The request may have arrived; no usable answer came back.
    Unknown(String),
}

/// What exists at the provider for a step whose outcome is not known.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Reconcile {
    /// It is there and it is ours (marker, size, digest as planned): take these ids.
    Adopt(RemoteIds),
    /// It is provably not there: the step may be attempted again.
    NotThere,
    /// Something else is in its place (another marker, other bytes under the same name): never touched.
    Conflict(String),
    /// It cannot be told (the lookup failed, or several things match): a person has to look.
    Ambiguous(String),
}

pub trait PublishProvider {
    fn name(&self) -> &str;
    fn execute(&self, ctx: &StepCtx) -> StepResult;
    fn reconcile(&self, ctx: &StepCtx) -> Reconcile;
    /// `--resolve-step`: checks that `remote_id` really is this step's result (marker, destination, digest are read
    /// again, the id is not trusted) and returns the ids to record.
    fn verify(&self, ctx: &StepCtx, remote_id: &str) -> Reconcile;
    /// What a person should look up when a step needs manual reconciliation.
    fn lookup_hint(&self, ctx: &StepCtx) -> String;
}
