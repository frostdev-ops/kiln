//! A provider for tests: an in-memory "remote" that counts every mutation, with scripted failures per step.

use super::provider::{PublishProvider, Reconcile, RemoteIds, StepCtx, StepResult};
use std::cell::RefCell;
use std::collections::BTreeMap;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Script {
    /// The mutation happens at the remote; the answer is lost.
    LoseAnswer,
    /// The request never leaves.
    NotSent,
    /// The remote says no (a 401, a 403, a rate limit).
    Refuse,
    /// The mutation happens, the answer is lost, and the lookup afterwards cannot tell.
    LoseAnswerAndLookup,
}

#[derive(Default)]
pub struct FakeProvider {
    /// What exists at the remote: step id -> the attempt that made it.
    pub remote: RefCell<BTreeMap<String, String>>,
    /// Mutations per step id (a second one for the same step is the bug the engine exists to prevent).
    pub mutations: RefCell<BTreeMap<String, u32>>,
    /// One-shot scripts per step id, consumed in order.
    pub scripts: RefCell<BTreeMap<String, Vec<Script>>>,
    pub lookups_fail: RefCell<bool>,
    /// The token a real provider would send; tests grep everything written for it.
    pub token: String,
}

impl FakeProvider {
    pub fn new() -> FakeProvider {
        FakeProvider { token: "ghp_FAKEFAKEFAKE0123456789".into(), ..Default::default() }
    }
    pub fn script(&self, step: &str, s: Script) {
        self.scripts.borrow_mut().entry(step.to_string()).or_default().push(s);
    }
    pub fn mutations_of(&self, step: &str) -> u32 {
        self.mutations.borrow().get(step).copied().unwrap_or(0)
    }
    fn ids(step: &str) -> RemoteIds {
        RemoteIds::from([("id".to_string(), format!("remote-{step}"))])
    }
    fn mutate(&self, step: &str, attempt: &str) {
        *self.mutations.borrow_mut().entry(step.to_string()).or_default() += 1;
        self.remote.borrow_mut().insert(step.to_string(), attempt.to_string());
    }
}

impl PublishProvider for FakeProvider {
    fn name(&self) -> &str {
        "fake"
    }
    fn execute(&self, ctx: &StepCtx) -> StepResult {
        let step = ctx.op.id.as_str();
        let script = {
            let mut s = self.scripts.borrow_mut();
            s.get_mut(step).filter(|v| !v.is_empty()).map(|v| v.remove(0))
        };
        match script {
            Some(Script::NotSent) => StepResult::NotSent("no connection".into()),
            Some(Script::Refuse) => StepResult::Refused("the provider refused the credential (401)".into()),
            Some(Script::LoseAnswer) => {
                self.mutate(step, ctx.attempt);
                StepResult::Unknown("the answer was lost".into())
            }
            Some(Script::LoseAnswerAndLookup) => {
                self.mutate(step, ctx.attempt);
                *self.lookups_fail.borrow_mut() = true;
                StepResult::Unknown("the answer was lost".into())
            }
            None => {
                self.mutate(step, ctx.attempt);
                StepResult::Confirmed(Self::ids(step))
            }
        }
    }
    fn reconcile(&self, ctx: &StepCtx) -> Reconcile {
        if *self.lookups_fail.borrow() {
            return Reconcile::Ambiguous("the lookup failed".into());
        }
        match self.remote.borrow().get(&ctx.op.id) {
            Some(by) if by == ctx.attempt => Reconcile::Adopt(Self::ids(&ctx.op.id)),
            Some(_) => Reconcile::Conflict("another attempt's result is there".into()),
            None => Reconcile::NotThere,
        }
    }
    fn verify(&self, ctx: &StepCtx, remote_id: &str) -> Reconcile {
        match self.remote.borrow().get(&ctx.op.id) {
            Some(_) if remote_id == format!("remote-{}", ctx.op.id) => Reconcile::Adopt(Self::ids(&ctx.op.id)),
            Some(_) => Reconcile::Conflict("that id belongs to something else".into()),
            None => Reconcile::NotThere,
        }
    }
    fn lookup_hint(&self, ctx: &StepCtx) -> String {
        format!("look for {} at the fake remote.", ctx.op.id)
    }
}
