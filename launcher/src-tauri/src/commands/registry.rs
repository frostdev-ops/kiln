//! The registry snapshot (read-only) and the registry plan's rollback (kiln_manage::registry_plan). No caller-provided
//! file name or game mapping crosses these commands: the game folder comes from the game id.
use crate::state::blocking;
use kiln_manage::registry_plan::{self as rp, Change, PlanMod, Request};
use kiln_manage::{Error, ErrorCode};
use serde::Serialize;
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::Path;
#[path = "registry_report_file.rs"]
mod file;

#[tauri::command]
pub async fn registry_report_read(game_id: String) -> Result<Option<String>, Error> {
    blocking(move || {
        let game = crate::state::game(&game_id)?;
        file::read_report(&game.path)
            .map_err(|e| Error::new(ErrorCode::Io, format!("Registry report could not be read: {e}")))
    }).await
}

/// The plan a rollback goes to.
#[derive(Debug, Serialize, PartialEq)]
pub struct Target {
    pub hash: String,
    pub mods: Vec<PlanMod>,
}

/// What the plan card shows (launcher/src/registry/api.ts RegistryRollback).
#[derive(Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Rollback {
    pub plan: Option<String>,
    pub previous: Option<String>,
    pub rollback: Option<Value>,
    pub pending: Option<Request>,
    pub target: Option<Target>,
    pub changes: Vec<Change>,
    pub changes_unknown: Option<String>,
    pub blocked: Option<String>,
    pub dry_run: bool,
    pub wrote: bool,
    pub cleared: Option<bool>,
}

/// As `kilnctl registry rollback` (runner/kilnctl/src/registry.rs): roll back to the previous (last good) plan of the last
/// start. `dry_run`: what changes back, nothing written. `clear`: the request goes. Saves are never touched.
pub fn rollback(game: &Path, clear: bool, dry_run: bool) -> Result<Rollback, Error> {
    let mut cleared = None;
    if clear && !dry_run {
        cleared = Some(rp::clear(game)?);
    }
    let c = rp::current(game)?;
    let mut out = Rollback {
        plan: c.plan.clone(), previous: c.previous.clone(), rollback: c.rollback.clone(), pending: rp::pending(game)?,
        target: None, changes: vec![], changes_unknown: None, blocked: None, dry_run, wrote: false, cleared,
    };
    if clear {
        return Ok(out);
    }
    let (from, to) = match (&c.plan, &c.previous) {
        (None, _) => {
            out.blocked = Some(format!("No registry plan yet: {} names none (start the game once with Kiln).", rp::REPORT_REL));
            return Ok(out);
        }
        (Some(_), None) => {
            out.blocked = Some("No earlier good plan to roll back to: a plan becomes good when a world starts on it without a refused edit.".into());
            return Ok(out);
        }
        (Some(f), Some(t)) if f == t => {
            out.blocked = Some("The previous plan is the one the last start ran on: nothing to roll back to.".into());
            return Ok(out);
        }
        (Some(f), Some(t)) => (f.clone(), t.clone()),
    };
    let target = match rp::plan(game, &to) {
        Ok(p) => p,
        Err(e) => {
            out.blocked = Some(e.message);
            return Ok(out);
        }
    };
    match rp::plan(game, &from) {
        Ok(now) => out.changes = rp::changes(&now, &target),
        Err(e) => out.changes_unknown = Some(e.message),
    }
    out.target = Some(Target { hash: target.hash.clone(), mods: target.mods.clone() });
    if !dry_run {
        out.pending = Some(rp::ask(game, &to, Some(&from), "launcher")?);
        out.wrote = true;
    }
    Ok(out)
}

#[tauri::command]
pub async fn registry_rollback(game_id: String, clear: bool, dry_run: bool) -> Result<Rollback, Error> {
    blocking(move || {
        let game = crate::state::game(&game_id)?;
        rollback(&game.path, clear, dry_run)
    }).await
}

/// Effect replay's opt-in file (kiln_manage::registry_plan::REPLAY_OPT_IN_REL, the one `kilnctl registry replay` writes): the
/// packages it names, true when opted in. The Conflicts page shows a toggle per package registry.json's `replay` lists.
#[tauri::command]
pub async fn registry_replay_opt_ins(game_id: String) -> Result<BTreeMap<String, bool>, Error> {
    blocking(move || {
        let game = crate::state::game(&game_id)?;
        rp::replay_opt_ins(&game.path)
    }).await
}

/// Opts one package (`shroudforge:<id>` / `eml:<id>`) in to effect replay or out of it; the next start reads it.
pub fn replay_opt(game: &Path, who: &str, on: bool) -> Result<BTreeMap<String, bool>, Error> {
    if !rp::replay_package_ok(who) {
        return Err(Error::invalid(format!("{who:?} is not shroudforge:<id> or eml:<id>")));
    }
    rp::set_replay_opt_in(game, who, on)
}

#[tauri::command]
pub async fn registry_replay_opt(game_id: String, who: String, on: bool) -> Result<BTreeMap<String, bool>, Error> {
    blocking(move || {
        let game = crate::state::game(&game_id)?;
        replay_opt(&game.path, &who, on)
    }).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    struct Game(std::path::PathBuf);
    impl Game {
        fn new(name: &str) -> Self {
            let root = std::env::temp_dir().join(format!("kiln-launcher-rollback-{}-{name}", std::process::id()));
            let _ = std::fs::remove_dir_all(&root);
            std::fs::create_dir_all(root.join(rp::DATA_REL).join("plans")).unwrap();
            std::fs::create_dir_all(root.join("kiln/state")).unwrap();
            Self(root)
        }
        fn plan(&self, hash: &str, values: Value) {
            let doc = json!({"format": 2, "key": {"side": "client", "exeSha256": "e".repeat(64), "kiln": "0.2.0-test"},
                "inputs": [{"mod": "bigpack", "version": "1.0.0", "batches": []}], "values": values, "previous": null});
            std::fs::write(rp::data_dir(&self.0).join("plans").join(format!("{hash}.json")), doc.to_string()).unwrap();
        }
        fn report(&self, plan: &str, previous: Option<&str>) {
            std::fs::write(self.0.join(rp::REPORT_REL), json!({"plan": {"hash": plan, "previous": previous}, "phase": "worldLoad"}).to_string()).unwrap();
        }
    }
    impl Drop for Game {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    fn h(c: char) -> String {
        std::iter::repeat_n(c, 64).collect()
    }

    #[test]
    fn the_dry_run_says_what_changes_back_and_writes_nothing() {
        let g = Game::new("dry");
        g.plan(&h('a'), json!({"items[game:plank].maxStack": 80}));
        g.plan(&h('b'), json!({"items[game:plank].maxStack": 160, "items[game:logs].maxStack": 30}));
        g.report(&h('b'), Some(&h('a')));
        let r = rollback(&g.0, false, true).unwrap();
        assert_eq!((r.plan.as_deref(), r.previous.as_deref(), r.blocked.as_deref()), (Some(&*h('b')), Some(&*h('a')), None));
        assert_eq!(r.target.as_ref().map(|t| t.hash.clone()), Some(h('a')));
        assert_eq!(r.changes.len(), 2);
        assert!(!r.wrote && r.pending.is_none() && rp::pending(&g.0).unwrap().is_none(), "a dry run writes nothing");
        let w = rollback(&g.0, false, false).unwrap();
        assert!(w.wrote);
        assert_eq!(rp::pending(&g.0).unwrap().map(|q| (q.plan, q.from, q.by)), Some((h('a'), Some(h('b')), "launcher".to_owned())));
        let s = serde_json::to_value(&w).unwrap();
        assert!(s.get("changesUnknown").is_some() && s.get("dryRun") == Some(&json!(false)), "camelCase for the page");
        let c = rollback(&g.0, true, false).unwrap();
        assert_eq!(c.cleared, Some(true));
        assert!(c.pending.is_none() && rp::pending(&g.0).unwrap().is_none());
        assert_eq!(rollback(&g.0, true, false).unwrap().cleared, Some(false), "nothing left to clear");
    }

    #[test]
    fn a_rollback_that_cannot_be_asked_for_says_why() {
        let g = Game::new("blocked");
        assert!(rollback(&g.0, false, true).unwrap().blocked.unwrap().contains("No registry plan yet"));
        g.report(&h('b'), None);
        assert!(rollback(&g.0, false, false).unwrap().blocked.unwrap().contains("No earlier good plan"));
        g.report(&h('b'), Some(&h('a')));
        let r = rollback(&g.0, false, false).unwrap();
        assert!(r.blocked.unwrap().contains("not in kiln/data/content/registry/plans"), "the previous plan's file is gone");
        assert!(!r.wrote && rp::pending(&g.0).unwrap().is_none());
        g.plan(&h('a'), json!({}));
        let r = rollback(&g.0, false, true).unwrap();
        assert!(r.blocked.is_none() && r.changes_unknown.is_some(), "the plan rolled away from is gone: the changes are unknown, the rollback still possible");
    }

    #[test]
    fn the_replay_toggle_writes_the_file_kilnctl_writes() {
        let g = Game::new("replay");
        assert_eq!(replay_opt(&g.0, "shroudforge:betterstacks", true).unwrap().get("shroudforge:betterstacks"), Some(&true));
        assert_eq!(rp::replay_opt_ins(&g.0).unwrap().get("shroudforge:betterstacks"), Some(&true), "kiln_manage's file");
        assert_eq!(replay_opt(&g.0, "shroudforge:betterstacks", false).unwrap().get("shroudforge:betterstacks"), Some(&false));
        assert!(replay_opt(&g.0, "../x", true).is_err() && replay_opt(&g.0, "betterstacks", true).is_err(), "only a package name crosses");
    }
}
