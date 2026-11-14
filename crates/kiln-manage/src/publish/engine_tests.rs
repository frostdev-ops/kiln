//! Fault injection over the publish engine: a crash at every before-send and after-send boundary of every step,
//! lost answers, refusals, and what a resume does afterwards. The invariant: no step ever mutates the remote twice.

use super::*;
use crate::publish::fake_provider::{FakeProvider, Script};
use crate::publish::plan::{create, PlanRequest};
use std::io::Write;
use std::path::PathBuf;

struct Fx {
    root: PathBuf,
    data: PathBuf,
    loaded: Loaded,
}
impl Drop for Fx {
    fn drop(&mut self) {
        txdir::discard(&self.loaded.dir);
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn fx() -> Fx {
    let root = std::env::temp_dir().join(format!("kiln-publish-engine-{}", crate::util::uuid_v4().unwrap()));
    std::fs::create_dir_all(&root).unwrap();
    let zip = root.join("m.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&zip).unwrap());
    w.start_file("good-mod/kiln.mod.json", zip::write::SimpleFileOptions::default()).unwrap();
    w.write_all(br#"{"id": "good-mod", "version": "1.0.0", "apiVersion": "0.2"}"#).unwrap();
    w.finish().unwrap();
    let config = root.join("kiln.publish.json");
    std::fs::write(&config, r#"{"format": 1, "provider": "github", "github": {"repository": "example/good-mod", "tag": "v{version}", "commit": "0123456789abcdef0123456789abcdef01234567"}}"#).unwrap();
    let data = root.join("data");
    let p = create(&PlanRequest { data_root: &data, artifact: &zip, config: &config, provider: "github", detached: false }).unwrap();
    let loaded = plan::load(&data, &p.plan).unwrap();
    Fx { root, data, loaded }
}

fn run(f: &Fx, provider: &FakeProvider, fault: Fault, events: &mut Vec<Progress>) -> Result<PublishOutcome> {
    let mut sink = |p: &Progress| events.push(p.clone());
    Engine { provider, events: &mut sink, fault }.apply(&f.loaded)
}

fn steps(f: &Fx) -> Vec<String> {
    f.loaded.plan.operations.iter().map(|o| o.id.clone()).collect()
}

fn journal(f: &Fx) -> Journal {
    crate::util::read_json(&f.loaded.dir.join(txdir::JOURNAL)).unwrap().unwrap()
}

#[test]
fn a_clean_apply_confirms_every_step_once_and_stops_at_the_draft() {
    let f = fx();
    let p = FakeProvider::new();
    let mut ev = Vec::new();
    let o = run(&f, &p, &no_faults, &mut ev).unwrap();
    assert!(o.ok);
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert_eq!(o.confirmed_steps, steps(&f));
    assert!(o.unknown_steps.is_empty() && o.manual_steps.is_empty() && o.reason.is_none());
    for s in steps(&f) {
        assert_eq!(p.mutations_of(&s), 1, "{s}");
        assert_eq!(o.remote_ids[&s]["id"], format!("remote-{s}"));
    }
    assert_eq!(p.mutations_of(PUBLISH_STEP), 0, "apply never publishes");
    // one event per durable state change: intent then confirmed, per step, in order
    let seen: Vec<(String, StepState)> = ev.iter().map(|e| (e.step.clone(), e.state)).collect();
    let want: Vec<(String, StepState)> = steps(&f).into_iter().flat_map(|s| [(s.clone(), StepState::IntentDurable), (s, StepState::Confirmed)]).collect();
    assert_eq!(seen, want);
    // applying again does nothing
    let again = run(&f, &p, &no_faults, &mut ev).unwrap();
    assert_eq!(again.state, OutcomeState::DraftReady);
    assert!(steps(&f).iter().all(|s| p.mutations_of(s) == 1));
}

/// The intent is on disk before the request: at the before-send boundary the journal already says intent_durable
/// with an attempt id, and the remote has seen nothing.
#[test]
fn the_intent_is_durable_before_anything_is_sent() {
    let f = fx();
    let p = FakeProvider::new();
    let dir = f.loaded.dir.clone();
    let seen = std::cell::RefCell::new(Vec::new());
    let check = |step: &str, b: Boundary| -> Result<()> {
        if b == Boundary::BeforeSend {
            let j: Journal = crate::util::read_json(&dir.join(txdir::JOURNAL))?.unwrap();
            let s = j.steps.iter().find(|s| s.id == step).unwrap().clone();
            seen.borrow_mut().push((s.state, s.attempt_id.is_some(), p.mutations_of(step)));
        }
        Ok(())
    };
    run(&f, &p, &check, &mut Vec::new()).unwrap();
    assert_eq!(seen.borrow().len(), steps(&f).len());
    assert!(seen.borrow().iter().all(|x| *x == (StepState::IntentDurable, true, 0)), "{:?}", seen.borrow());
}

/// A crash at every boundary of every step, then a resume: the end state is the complete draft and no step has
/// mutated the remote twice.
#[test]
fn a_crash_at_any_boundary_resumes_without_a_duplicate() {
    let template = fx();
    for step in steps(&template) {
        for boundary in [Boundary::BeforeSend, Boundary::AfterSend] {
            let f = fx();
            let p = FakeProvider::new();
            let crash = |s: &str, b: Boundary| -> Result<()> {
                if s == step && b == boundary { Err(Error::new(ErrorCode::Canceled, "simulated crash")) } else { Ok(()) }
            };
            let e = run(&f, &p, &crash, &mut Vec::new()).unwrap_err();
            assert_eq!(e.code, ErrorCode::Canceled);
            // what the crash left behind: the step is intent_durable, nothing later was touched
            let j = journal(&f);
            let at = j.steps.iter().position(|s| s.id == step).unwrap();
            assert_eq!(j.steps[at].state, StepState::IntentDurable, "{step} {boundary:?}");
            assert!(j.steps[at + 1..].iter().all(|s| s.state == StepState::Planned));
            assert_eq!(p.mutations_of(&step), if boundary == Boundary::AfterSend { 1 } else { 0 });
            // resume
            let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
            assert_eq!(o.state, OutcomeState::DraftReady, "{step} {boundary:?}: {:?}", o.reason);
            for s in steps(&f) {
                assert_eq!(p.mutations_of(&s), 1, "{s} mutated {} times after a crash at {step} {boundary:?}", p.mutations_of(&s));
            }
        }
    }
    println!("PASS publish.resume");
}

#[test]
fn a_lost_answer_is_reconciled_not_resent() {
    for step in steps(&fx()) {
        let f = fx();
        let p = FakeProvider::new();
        p.script(&step, Script::LoseAnswer);
        let mut ev = Vec::new();
        let o = run(&f, &p, &no_faults, &mut ev).unwrap();
        assert_eq!(o.state, OutcomeState::DraftReady, "{step}");
        assert_eq!(p.mutations_of(&step), 1, "{step} was sent again after a lost answer");
        assert!(ev.iter().any(|e| e.step == step && e.state == StepState::OutcomeUnknown));
    }
}

#[test]
fn an_undecidable_outcome_stops_for_a_person_and_is_never_called_a_clean_failure() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    p.script(&all[1], Script::LoseAnswerAndLookup);
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert!(!o.ok);
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    assert_eq!(o.confirmed_steps, [all[0].clone()]);
    assert_eq!(o.unknown_steps, [all[1].clone()]);
    assert_eq!(o.manual_steps.len(), 1);
    assert!(o.manual_steps[0].contains("--resolve-step") && o.manual_steps[0].contains(&all[1]));
    assert_eq!(p.mutations_of(&all[2]), 0, "nothing after the unknown step is sent");
    // a resume does not touch it either
    *p.lookups_fail.borrow_mut() = false;
    let again = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert_eq!(again.state, OutcomeState::ManualReconcile);
    assert_eq!(p.mutations_of(&all[1]), 1);
    // resolve: a wrong id is refused, the right one is verified and adopted, then the resume finishes
    let mut sink = |_: &Progress| {};
    let mut engine = Engine { provider: &p, events: &mut sink, fault: &no_faults };
    assert_eq!(engine.resolve_step(&f.loaded, &all[1], "remote-something-else").unwrap_err().code, ErrorCode::Refused);
    assert_eq!(engine.resolve_step(&f.loaded, &all[0], "remote-draft").unwrap_err().code, ErrorCode::Refused);
    assert_eq!(engine.resolve_step(&f.loaded, "no-such-step", "x").unwrap_err().code, ErrorCode::NotFound);
    let o = engine.resolve_step(&f.loaded, &all[1], &format!("remote-{}", all[1])).unwrap();
    assert_eq!(o.state, OutcomeState::Partial);
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert!(all.iter().all(|s| p.mutations_of(s) == 1));
}

#[test]
fn refusals_and_unsent_requests_keep_what_was_confirmed_and_resume_later() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    p.script(&all[0], Script::Refuse);
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert!(!o.ok);
    assert_eq!(o.state, OutcomeState::Refused);
    assert!(o.confirmed_steps.is_empty() && o.reason.as_deref().unwrap().contains("401"));
    assert_eq!(p.mutations_of(&all[0]), 0);
    p.script(&all[2], Script::NotSent);
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert_eq!(o.state, OutcomeState::Partial);
    assert_eq!(o.confirmed_steps, [all[0].clone(), all[1].clone()]);
    assert!(o.unknown_steps.is_empty());
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert!(all.iter().all(|s| p.mutations_of(s) == 1));
}

#[test]
fn a_changed_frozen_file_stops_the_step_before_it_is_sent() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    let dir = f.loaded.dir.clone();
    // the artifact is swapped after the first step went out
    let tamper = |s: &str, b: Boundary| -> Result<()> {
        if s == "draft" && b == Boundary::AfterSend {
            let path = dir.join(txdir::ARTIFACT);
            let mut perm = std::fs::metadata(&path).unwrap().permissions();
            #[allow(clippy::permissions_set_readonly_false)]
            perm.set_readonly(false);
            std::fs::set_permissions(&path, perm).unwrap();
            std::fs::write(&path, b"another zip").unwrap();
        }
        Ok(())
    };
    let e = run(&f, &p, &tamper, &mut Vec::new()).unwrap_err();
    assert_eq!(e.code, ErrorCode::HashMismatch);
    assert_eq!(p.mutations_of(&all[0]), 1);
    assert_eq!(p.mutations_of(&all[1]), 0, "the changed zip was not uploaded");
    assert_eq!(journal(&f).steps[1].state, StepState::Planned, "no intent was written for a step that could not start");
    println!("PASS publish.immutable-artifact (engine)");
}

/// Something that is not ours sits where a step's result would go: it is never adopted and never overwritten.
#[test]
fn a_foreign_result_is_a_conflict_for_a_person() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    let crash = |s: &str, b: Boundary| -> Result<()> {
        if s == all[1] && b == Boundary::BeforeSend { Err(Error::new(ErrorCode::Canceled, "simulated crash")) } else { Ok(()) }
    };
    run(&f, &p, &crash, &mut Vec::new()).unwrap_err();
    p.remote.borrow_mut().insert(all[1].clone(), "somebody-else".into());
    let o = run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    assert_eq!(p.mutations_of(&all[1]), 0, "a foreign result is not overwritten");
    assert_eq!(journal(&f).steps[1].state, StepState::ManualReconcile);
}

#[test]
fn publishing_the_draft_is_its_own_step_and_needs_the_whole_draft() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    p.script(&all[3], Script::NotSent);
    run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    let mut sink = |_: &Progress| {};
    let mut engine = Engine { provider: &p, events: &mut sink, fault: &no_faults };
    assert_eq!(engine.publish_draft(&f.loaded).unwrap_err().code, ErrorCode::Refused);
    assert_eq!(p.mutations_of(PUBLISH_STEP), 0);
    assert_eq!(engine.apply(&f.loaded).unwrap().state, OutcomeState::DraftReady);
    // the answer to the publish request is lost: reconciled, not sent again
    p.script(PUBLISH_STEP, Script::LoseAnswer);
    let o = engine.publish_draft(&f.loaded).unwrap();
    assert!(o.ok);
    assert_eq!(o.state, OutcomeState::Published);
    assert_eq!(p.mutations_of(PUBLISH_STEP), 1);
    assert_eq!(engine.publish_draft(&f.loaded).unwrap().state, OutcomeState::Published);
    assert_eq!(p.mutations_of(PUBLISH_STEP), 1);
    // a transaction is found by its id alone
    assert_eq!(load_by_id(&f.data, &f.loaded.plan.tx_id).unwrap().plan, f.loaded.plan);
}

#[test]
fn one_engine_at_a_time_and_a_foreign_journal_is_refused() {
    let f = fx();
    let p = FakeProvider::new();
    let held = lock(&f.loaded.dir).unwrap();
    assert_eq!(run(&f, &p, &no_faults, &mut Vec::new()).unwrap_err().code, ErrorCode::Busy);
    drop(held);
    run(&f, &p, &no_faults, &mut Vec::new()).unwrap();
    // a journal that says another plan, or whose step means something else
    let mut j = journal(&f);
    j.steps[1].semantic_hash = "0".repeat(64);
    j.persist(&f.loaded.dir).unwrap();
    assert_eq!(run(&f, &p, &no_faults, &mut Vec::new()).unwrap_err().code, ErrorCode::HashMismatch);
    let mut j = journal(&f);
    j.plan_digest = "0".repeat(64);
    j.persist(&f.loaded.dir).unwrap();
    assert_eq!(run(&f, &p, &no_faults, &mut Vec::new()).unwrap_err().code, ErrorCode::HashMismatch);
}

#[test]
fn nothing_written_holds_the_credential() {
    let f = fx();
    let p = FakeProvider::new();
    let all = steps(&f);
    p.script(&all[1], Script::LoseAnswer);
    p.script(&all[2], Script::Refuse);
    let mut ev = Vec::new();
    let o1 = run(&f, &p, &no_faults, &mut ev).unwrap();
    let o2 = run(&f, &p, &no_faults, &mut ev).unwrap();
    let mut text = serde_json::to_string(&ev).unwrap() + &serde_json::to_string(&o1).unwrap() + &serde_json::to_string(&o2).unwrap();
    for e in std::fs::read_dir(&f.loaded.dir).unwrap().flatten() {
        if e.file_name() != txdir::ARTIFACT {
            text += &String::from_utf8_lossy(&std::fs::read(e.path()).unwrap());
        }
    }
    assert!(text.contains("intent_durable") || text.contains("confirmed"));
    for needle in [p.token.as_str(), "ghp_", "Bearer", "Authorization", "X-Amz-Signature", "?token=", "&sig="] {
        assert!(!text.contains(needle), "{needle} was written");
    }
    println!("PASS publish.no-credential-leak (engine; the provider's request log is checked with the GitHub provider)");
}
