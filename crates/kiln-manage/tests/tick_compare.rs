// SPDX-License-Identifier: MIT
//! T12 (issue #57): the before/after tick-cost report over fixture files with known numbers, with and without the
//! `tasks` section (T4), malformed lines, different window counts, and mods present in one file only.

use kiln_manage::tick_compare::{compare, parse_text, summarize, summarize_file, WorldKey};
use std::path::PathBuf;

fn fixture(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixture/tick-compare").join(name)
}

fn key() -> WorldKey {
    WorldKey { world: "server".into(), dedicated: true }
}

fn close(a: Option<f64>, want: f64) {
    assert_eq!(a.map(|x| (x * 1e9).round() / 1e9), Some(want), "{a:?} != {want}");
}

#[test]
fn fixture_worlds_means_percentiles_and_over_budget() {
    let before = summarize_file(&fixture("before.jsonl")).unwrap();
    assert_eq!((before.lines, before.malformed), (2, 1), "two lines read, the malformed one counted");
    let w = &before.worlds[&key()];
    assert_eq!((w.windows, w.steps), (2, 200));
    // steps-weighted mean of the window p50s: (100*10 + 100*20) / 200
    close(w.step_mean_ms, 15.0);
    // worst window's p99, and the longest step anywhere
    close(w.step_p99_ms, 30.0);
    close(w.step_max_ms, 50.0);
    assert_eq!(w.budget_ms, Some(40.9));
    // the whole step (Pre + step + Post): steps-weighted p50 sums (100*11.5 + 100*23.5) / 200, and the longest
    // worst step taken apart (2 + 50 + 1)
    close(w.tick_mean_ms, 17.5);
    close(w.tick_max_ms, 53.0);
    // both windows' worst whole steps (53, and 40 + 3 + 1 = 44) are over the budget, though window 2's game step
    // never is (max 35); no part's p99 is over it, so at least one step in each
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (2, 2));

    let after = summarize_file(&fixture("after.jsonl")).unwrap();
    let w = &after.worlds[&key()];
    close(w.step_mean_ms, 6.0); // (100*5 + 100*7) / 200
    close(w.step_p99_ms, 10.0);
    close(w.step_max_ms, 12.0);
    close(w.tick_mean_ms, 6.85); // (100*5.75 + 100*7.95) / 200
    close(w.tick_max_ms, 18.5); // 7 + 11 + 0.5
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (0, 0));
}

#[test]
fn fixture_mods_mean_p99_max_and_share() {
    let before = summarize_file(&fixture("before.jsonl")).unwrap();
    let a = &before.mods[&(key(), "mod-a".into())];
    let pre = a.pre.as_ref().unwrap();
    assert_eq!((pre.windows, pre.steps), (2, 200));
    close(pre.mean_ms, 3.0); // (200+400)/200
    close(pre.p99_ms, 4.0); // per-window means 2 and 4, nearest-rank p99
    close(pre.max_ms, 40.0);
    close(pre.share_pct, 17.142857143); // 3.0 over the same windows' whole step mean 17.5
    let post = a.post.as_ref().unwrap();
    close(post.mean_ms, 1.0);
    close(post.p99_ms, 1.5);
    close(post.max_ms, 20.0);

    // a mod of one file only is in the summary, and in the comparison with the other side missing
    let only = &before.mods[&(key(), "mod-only-before".into())];
    close(only.pre.as_ref().unwrap().mean_ms, 1.0);
    close(only.pre.as_ref().unwrap().share_pct, 8.695652174); // 1.0 over window 1's whole step p50 1 + 10 + 0.5
    assert!(only.post.is_none());

    let after = summarize_file(&fixture("after.jsonl")).unwrap();
    let r = compare(before, after);
    let m = r.mods.iter().find(|m| m.owner == "mod-only-before").unwrap();
    assert!(m.pre.before.is_some() && m.pre.after.is_none() && m.pre.mean.delta.is_none());
    let m = r.mods.iter().find(|m| m.owner == "mod-a").unwrap();
    close(m.pre.mean.before, 3.0);
    close(m.pre.mean.after, 0.6); // (50+70)/200
    close(m.pre.mean.delta, -2.4);
    close(m.pre.mean.delta_pct, -80.0);
    close(m.pre.share.after, 8.759124088); // 0.6 over 6.85
    let w = &r.worlds[0];
    close(w.step_mean.delta_pct, -60.0); // 15 -> 6
    close(w.tick_mean.delta_pct, -60.857142857); // 17.5 -> 6.85
    close(w.tick_max.delta, -34.5); // 53 -> 18.5

    // the text names the one-file-only mod and both worlds' numbers
    let text = kiln_manage::tick_compare::render_text(&r);
    assert!(text.contains("mod-only-before") && text.contains("over budget >= 2 steps in 2 windows"), "{text}");
}

#[test]
fn tasks_section_present_in_one_file_only() {
    let before = summarize_file(&fixture("before.jsonl")).unwrap();
    assert!(before.tasks.is_none(), "no tasks section: skipped gracefully");
    let after = summarize_file(&fixture("after.jsonl")).unwrap();
    let t = after.tasks.as_ref().unwrap();
    assert_eq!(t.windows, 2);
    let o = &t.owners["mod-a"];
    assert_eq!((o.windows, o.completed, o.faulted, o.cancelled), (2, 30, 1, 1));
    close(Some(o.cpu_ms), 400.0);
    close(Some(o.wall_ms), 600.0);
    close(o.cpu_ms_per_window, 200.0);
    close(o.longest_ms, 20.0);
    assert_eq!(o.longest_name.as_deref(), Some("loot-scan"));
    let apply = t.apply.as_ref().unwrap();
    assert_eq!((apply.applied, apply.deferred, apply.cancelled, apply.backlog_max), (30, 5, 1, 5));
    close(apply.ms_per_window, 5.0);
    assert_eq!(t.hops_refused, 3);

    let r = compare(before, after);
    let tasks = r.tasks.as_ref().expect("one side has tasks: the comparison has the section");
    assert!(tasks.apply_before.is_none() && tasks.apply_after.is_some());
    assert!(tasks.owners[0].before.is_none() && tasks.owners[0].after.is_some());
    let text = kiln_manage::tick_compare::render_text(&r);
    assert!(text.contains("apply (after): 30 applied, 5 deferred"), "{text}");

    // neither file has one: no section at all
    let (a, _) = parse_text(r#"{"steps":10,"step_ms":{"p50":1.0,"p99":1.0,"max":1.0}}"#);
    let r = compare(summarize("a", &a, 0), summarize("b", &a, 0));
    assert!(r.tasks.is_none());
    assert!(kiln_manage::tick_compare::render_text(&r).contains("no tasks section in either file"));
}

#[test]
fn hosted_world_budget_and_over_budget_from_p99() {
    // a hosting client's server world: 16.8 ms budget; p99 (20) over it over 100 steps means at least 1 step over
    let line = r#"{"world":"server","steps":100,"step_ms":{"p50":10.0,"p99":20.0,"max":25.0},"pre_parts":{"subs":[]},"post_parts":{"subs":[]}}"#;
    let (stats, bad) = parse_text(&format!("{line}\n\n  \ngarbage\n"));
    assert_eq!((stats.len(), bad), (1, 1), "blank lines are not malformed");
    let s = summarize("hosted", &stats, bad);
    let k = WorldKey { world: "server".into(), dedicated: false };
    let w = &s.worlds[&k];
    assert_eq!(w.budget_ms, Some(16.8));
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (1, 1));
    assert_eq!(k.label(), "server (hosted)");

    // a client world has no defined budget: no over-budget numbers, but step stats still read
    let (stats, _) = parse_text(r#"{"world":"client","steps":60,"step_ms":{"p50":30.0,"p99":40.0,"max":41.0}}"#);
    let s = summarize("client", &stats, 0);
    let w = &s.worlds[&WorldKey { world: "client".into(), dedicated: false }];
    assert_eq!(w.budget_ms, None);
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (0, 0));
    close(w.step_mean_ms, 30.0);
}

#[test]
fn a_tasks_window_written_by_two_worlds_counts_once() {
    let tasks = r#""tasks":{"window":{"id":3},"owners":[{"owner":"mod-a","cpu_ms":10.0,"wall_ms":12.0,"completed":1}],"apply":{"applied":1,"deferred":0,"ms":1.0,"backlog":0}}"#;
    let text = format!(
        "{{\"world\":\"server\",\"steps\":10,\"step_ms\":{{\"p50\":1.0,\"p99\":1.0,\"max\":1.0}},{tasks}}}\n{{\"world\":\"client\",\"steps\":10,\"step_ms\":{{\"p50\":1.0,\"p99\":1.0,\"max\":1.0}},{tasks}}}"
    );
    let (stats, _) = parse_text(&text);
    let s = summarize("x", &stats, 0);
    let t = s.tasks.as_ref().unwrap();
    assert_eq!(t.windows, 1);
    close(Some(t.owners["mod-a"].cpu_ms), 10.0);
}

#[test]
fn missing_and_empty_windows_do_not_divide_by_zero() {
    let (stats, bad) = parse_text("");
    assert_eq!((stats.len(), bad), (0, 0));
    let s = summarize("empty", &stats, 0);
    assert!(s.worlds.is_empty() && s.mods.is_empty() && s.tasks.is_none());
    let r = compare(s.clone(), s);
    let text = kiln_manage::tick_compare::render_text(&r);
    assert!(text.contains("no valid tick-stats lines") && text.contains("(no windows in either file)"), "{text}");
    // a zero-step window: counted as a window, no mean, no share
    let (stats, _) = parse_text(r#"{"steps":0,"step_ms":{"p50":0.0,"p99":0.0,"max":0.0},"pre_parts":{"subs":[{"owner":"m","max":0.0,"total":0.0,"over50":0}]}}"#);
    let s = summarize("zero", &stats, 0);
    let w = &s.worlds[&key()];
    assert_eq!(w.windows, 1);
    assert_eq!(w.step_mean_ms, None);
    assert_eq!(s.mods[&(key(), "m".into())].pre.as_ref().unwrap().mean_ms, None);
    // the report is JSON with the derivation notes
    let v = serde_json::to_value(&r).unwrap();
    assert_eq!(v["ok"], true);
    assert!(v["notes"].as_array().is_some_and(|n| !n.is_empty()));
    // worlds and mods serialise as arrays (their map keys are not strings)
    let (stats, _) = parse_text(r#"{"steps":10,"step_ms":{"p50":1.0,"p99":2.0,"max":3.0},"pre_parts":{"subs":[{"owner":"m","max":1.0,"total":5.0,"over50":0}]}}"#);
    let v = serde_json::to_value(compare(summarize("a", &stats, 0), summarize("b", &stats, 0))).unwrap();
    assert_eq!(v["before"]["worlds"][0]["label"], "server (dedicated)");
    assert_eq!(v["before"]["worlds"][0]["stepMeanMs"], 1.0);
    assert_eq!(v["before"]["mods"][0]["owner"], "m");
    assert_eq!(v["mods"][0]["pre"]["mean"]["deltaPct"], 0.0);
}

#[test]
fn logs_tick_stat_reads_the_tasks_section_leniently() {
    // the shape of issue #49, read through logs::TickStat; a section that does not read costs only itself
    let line = r#"{"time":"t","steps":3,"tasks":{"window":{"id":7,"span_ms":60000,"age_ms":3},"pool":{"workers":3,"long_running":1,"queue_max":12},"owners":[{"owner":"kiln-auto-loot","queued":{"current":0,"max":4},"running":{"current":1,"max":2},"completed":120,"faulted":0,"cancelled":2,"cpu_ms":830.5,"wall_ms":910.2,"longest_ms":44.1,"longest_name":"loot-scan","queued_too_long":0,"running_too_long":0,"queue_full":0,"unload_blocked":0}],"apply":{"applied":96,"deferred":3,"cancelled":0,"ms":4.2,"owner_budget_stops":1,"step_budget_stops":0,"owners_served":1,"backlog":0},"hops":{"refused":0}}}"#;
    let v = kiln_manage::logs::tick_stats(line);
    let t = v[0].tasks.as_ref().expect("tasks");
    assert_eq!(t.window.as_ref().and_then(|w| w.id), Some(7));
    assert_eq!(t.owners[0].owner, "kiln-auto-loot");
    assert_eq!((t.owners[0].cpu_ms, t.owners[0].wall_ms), (830.5, 910.2));
    assert_eq!(t.apply.as_ref().map(|a| (a.applied, a.deferred)), Some((96, 3)));
    let odd = kiln_manage::logs::tick_stats(r#"{"time":"t","steps":3,"tasks":"later"}"#);
    assert_eq!(odd[0].steps, 3);
    assert!(odd[0].tasks.is_none());
}

#[test]
fn json_that_is_not_a_tick_stats_line_is_malformed() {
    // every field of TickStat has a default, so these would otherwise read as phantom zero-step windows
    let (stats, bad) = parse_text("{}\n[]\n{\"level\":\"info\"}\n42\n\"steps\"\n");
    assert_eq!((stats.len(), bad), (0, 5));
}

#[test]
fn over_budget_judges_the_whole_step_pre_step_and_post() {
    // The budget covers Pre + the game's step + Post (the mods' handlers run inside it). Here the game's step alone
    // never reaches 40.9 ms, but the worst step taken apart (Pre 20 + step 30 + Post 5 = 55) does, and Pre's p99
    // alone is over the budget: at least steps - floor(0.99 * steps) = 2 steps of 200 were over.
    let line = r#"{"time":"t","steps":200,"step_ms":{"p50":10.000,"p99":30.000,"max":30.000},"pre_ms":{"p50":1.0000,"p99":45.0000,"max":50.000},"post_ms":{"p50":0.5000,"p99":1.0000,"max":5.000},"post_parts":{"subs":[]},"pre_parts":{"subs":[]},"worst":{"at":"12:00:00.000","pre":20.000,"step":30.000,"post":5.000,"post_subs":[],"pre_subs":[]}}"#;
    let (stats, bad) = parse_text(line);
    assert_eq!((stats.len(), bad), (1, 0));
    let s = summarize("x", &stats, 0);
    let w = &s.worlds[&key()];
    close(w.step_max_ms, 30.0);
    close(w.tick_max_ms, 55.0); // the worst step: 20 + 30 + 5
    close(w.tick_mean_ms, 11.5); // p50s: 1 + 10 + 0.5
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (1, 2));

    // an older line without `worst`: the longest part is the least the longest whole step can have been
    let (stats, _) = parse_text(r#"{"time":"t","steps":100,"step_ms":{"p50":10.0,"p99":20.0,"max":30.0},"pre_ms":{"p50":1.0,"p99":2.0,"max":42.0},"post_ms":{"p50":0.0,"p99":0.0,"max":0.0}}"#);
    let w = &summarize("old", &stats, 0).worlds[&key()];
    close(w.tick_max_ms, 42.0);
    assert_eq!((w.windows_over_budget, w.steps_over_budget_min), (1, 1));
}
