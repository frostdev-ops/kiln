// SPDX-License-Identifier: MIT
//! Before/after tick-cost report from two `tick-stats.jsonl` files (issue #57, T12 of the tasks plan):
//! did moving a mod's work into Kiln tasks actually help the step?
//!
//! The method, and how to run a fair pair, is in `docs/kiln/kilnctl.md` ("Measuring a task change"). This module is
//! the reader and the arithmetic; `kilnctl tick-compare` is the thin command on top.
//!
//! ## What the file does and does not hold
//!
//! One line is one ~60 s window of one world (`native/core/src/tick_stats.h`): `step_ms`/`pre_ms`/`post_ms` are
//! p50/p99/max of that window, a subscriber part is `{max, total, over50}` summed over the window, and there are no
//! per-step samples. So some numbers below are exact and some are the best a window summary allows, and the report
//! never pretends otherwise:
//!
//! - exact: a part's `max` (the longest call), a part's mean per step (`total / steps`, one Pre/Post call per step),
//!   the sums of the `tasks` counters, and every maximum;
//! - window aggregates: the step's mean is the steps-weighted mean of the windows' `step_ms.p50`, its p99 is the
//!   worst window's p99 (the pooled p99 cannot be rebuilt from window p99s), and a mod's p99 is the p99 of its
//!   per-window means (`total / steps` of each window). A mod's share of the step is its mean per step over the
//!   step mean of the same windows, so it inherits the p50 proxy;
//! - over budget is a lower bound, not a count: a window whose `max` is over the budget had at least one step over
//!   it, and a window whose p99 is over it had at least `steps - floor(0.99 * steps)` (the native percentile picks
//!   that element, `tick.cpp` `pct`). The exact count is not in the file; [`WorldSummary::steps_over_budget_min`]
//!   is the least it can be, with [`WorldSummary::windows_over_budget`] beside it.
//!
//! Budgets: 40.9 ms for a dedicated server (its lines have no `world`), 16.8 ms for a hosting client's server
//! world (`"world":"server"`). Other worlds (`client`, `prediction`) get no budget: none is defined for them.
//!
//! `pre_parts` lists only the eight Pre subscribers with the longest call of a window, so a Pre owner missing from
//! a window was slower than eight others there (or had no Pre subscriber), not necessarily idle; its mean and
//! share are over the windows that list it. `post_parts` lists every Post owner. The `tasks` section (T4, #49) is
//! optional: absent lines are skipped for that section, and a window several worlds write is counted once when
//! its `tasks.window.id` repeats.

use crate::logs::{TaskOwner, TickStat};
use crate::{Error, Result};
use serde::Serialize;
use std::collections::{BTreeMap, HashSet};
use std::path::Path;

/// The dedicated server's step budget (ms): ~24.5 Hz.
pub const DEDICATED_BUDGET_MS: f64 = 40.9;
/// A hosting client's server-world step budget (ms).
pub const HOSTED_BUDGET_MS: f64 = 16.8;

/// One world of a file: the `world` field as written (`server` when absent), and dedicated-server lines apart from
/// a hosting client's `"server"` world (they have different budgets).
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Serialize)]
pub struct WorldKey {
    pub world: String,
    pub dedicated: bool,
}

impl Default for WorldKey {
    fn default() -> WorldKey {
        WorldKey { world: "server".into(), dedicated: true }
    }
}

impl WorldKey {
    fn of(stat: &TickStat) -> WorldKey {
        match &stat.world {
            None => WorldKey { world: "server".into(), dedicated: true },
            Some(w) => WorldKey { world: w.clone(), dedicated: false },
        }
    }
    /// `server (dedicated)`, `server (hosted)`, or the world as written.
    pub fn label(&self) -> String {
        if self.dedicated {
            "server (dedicated)".into()
        } else if self.world == "server" {
            "server (hosted)".into()
        } else {
            self.world.clone()
        }
    }
    fn budget_ms(&self) -> Option<f64> {
        if self.dedicated {
            Some(DEDICATED_BUDGET_MS)
        } else if self.world == "server" {
            Some(HOSTED_BUDGET_MS)
        } else {
            None
        }
    }
}

/// Nearest-rank percentile of `values` at `q` (0.99 = p99); None when empty. The values are sorted in place.
pub fn percentile(values: &mut [f64], q: f64) -> Option<f64> {
    if values.is_empty() {
        return None;
    }
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let rank = (q.clamp(0.0, 1.0) * values.len() as f64).ceil() as usize;
    Some(values[rank.saturating_sub(1).min(values.len() - 1)])
}

#[cfg(test)]
fn mean(values: &[f64]) -> Option<f64> {
    (!values.is_empty()).then(|| values.iter().sum::<f64>() / values.len() as f64)
}

/// The least number of steps of one window that were over `budget`, from its p99 and max alone (see module docs).
fn steps_over_min(steps: u64, p99: f64, max: f64, budget: f64) -> u64 {
    if steps == 0 || max <= budget {
        0
    } else if p99 > budget {
        // pct(v, 0.99) is element floor(0.99 * n): that element and every later one are >= p99.
        steps - ((0.99 * steps as f64) as u64).min(steps - 1)
    } else {
        1
    }
}

/// One world's step time over a whole file.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorldSummary {
    pub windows: u64,
    pub steps: u64,
    /// Steps-weighted mean of the windows' `step_ms.p50` (the file holds no step total; see module docs).
    pub step_mean_ms: Option<f64>,
    /// The worst window's `step_ms.p99`.
    pub step_p99_ms: Option<f64>,
    pub step_max_ms: Option<f64>,
    pub budget_ms: Option<f64>,
    /// Windows whose longest step was over the budget (at least one step over).
    pub windows_over_budget: u64,
    /// Lower bound on the steps over the budget (see module docs).
    pub steps_over_budget_min: u64,
}

/// One owner's Pre or Post time in one world over a whole file.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PhaseSummary {
    /// Windows that list the owner (for Pre: of the eight slowest; see module docs).
    pub windows: u64,
    /// The steps of those windows.
    pub steps: u64,
    pub total_ms: f64,
    /// `total_ms / steps`: its mean time per step in the windows that list it.
    pub mean_ms: Option<f64>,
    /// p99 of the per-window means (`total / steps` of each window), nearest-rank.
    pub p99_ms: Option<f64>,
    /// The longest single call.
    pub max_ms: Option<f64>,
    /// Calls longer than 50 ms, summed.
    pub over50: u64,
    /// `mean_ms` as a percentage of the step mean of the same windows (which uses the p50 proxy).
    pub share_pct: Option<f64>,
}

/// One owner's Pre and Post in one world.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct ModSummary {
    pub pre: Option<PhaseSummary>,
    pub post: Option<PhaseSummary>,
}

/// One owner's `tasks` numbers over a whole file (T4 counters summed over its windows).
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskOwnerSummary {
    pub windows: u64,
    pub cpu_ms: f64,
    pub wall_ms: f64,
    pub cpu_ms_per_window: Option<f64>,
    pub wall_ms_per_window: Option<f64>,
    pub longest_ms: Option<f64>,
    pub longest_name: Option<String>,
    pub completed: u64,
    pub faulted: u64,
    pub cancelled: u64,
    pub queued_too_long: u64,
    pub running_too_long: u64,
    pub queue_full: u64,
}

/// The `tasks.apply` counters over a whole file.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplySummary {
    pub windows: u64,
    pub applied: u64,
    /// Items that waited for a later step (the deferred items of the report).
    pub deferred: u64,
    pub cancelled: u64,
    pub ms: f64,
    pub ms_per_window: Option<f64>,
    pub owner_budget_stops: u64,
    pub step_budget_stops: u64,
    pub backlog_max: u64,
}

/// A file's `tasks` section, when any line has one.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TasksSummary {
    /// Windows counted (a `tasks.window.id` several worlds wrote counts once).
    pub windows: u64,
    pub owners: BTreeMap<String, TaskOwnerSummary>,
    pub apply: Option<ApplySummary>,
    pub hops_refused: u64,
}

/// One parsed `tick-stats.jsonl` file, summarised.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FileSummary {
    pub label: String,
    /// Lines that read as a tick-stats line.
    pub lines: u64,
    /// Non-blank lines that did not read; skipped, and counted here.
    pub malformed: u64,
    #[serde(serialize_with = "ser_worlds")]
    pub worlds: BTreeMap<WorldKey, WorldSummary>,
    /// (world, owner) -> its Pre/Post. An owner is a tick-stats owner name (`service:…`, `plugin:…`, a mod id, …).
    #[serde(serialize_with = "ser_mods")]
    pub mods: BTreeMap<(WorldKey, String), ModSummary>,
    pub tasks: Option<TasksSummary>,
}

fn ser_worlds<S: serde::Serializer>(m: &BTreeMap<WorldKey, WorldSummary>, s: S) -> std::result::Result<S::Ok, S::Error> {
    use serde::ser::SerializeSeq;
    #[derive(Serialize)]
    struct Entry<'a> {
        world: &'a WorldKey,
        label: String,
        #[serde(flatten)]
        summary: &'a WorldSummary,
    }
    let mut seq = s.serialize_seq(Some(m.len()))?;
    for (k, v) in m {
        seq.serialize_element(&Entry { world: k, label: k.label(), summary: v })?;
    }
    seq.end()
}

fn ser_mods<S: serde::Serializer>(
    m: &BTreeMap<(WorldKey, String), ModSummary>,
    s: S,
) -> std::result::Result<S::Ok, S::Error> {
    use serde::ser::SerializeSeq;
    #[derive(Serialize)]
    struct Entry<'a> {
        world: &'a WorldKey,
        label: String,
        owner: &'a str,
        #[serde(flatten)]
        summary: &'a ModSummary,
    }
    let mut seq = s.serialize_seq(Some(m.len()))?;
    for ((k, owner), v) in m {
        seq.serialize_element(&Entry { world: k, label: k.label(), owner, summary: v })?;
    }
    seq.end()
}

struct PhaseAcc {
    windows: u64,
    steps: u64,
    total_ms: f64,
    window_means: Vec<f64>,
    max_ms: f64,
    over50: u64,
    // the step mean of the same windows: sum of steps * step_ms.p50, and its steps
    step_weighted_p50: f64,
}

impl PhaseAcc {
    fn add(&mut self, stat: &TickStat, total: f64, max: f64, over50: u64) {
        self.windows += 1;
        self.steps += stat.steps;
        self.total_ms += total;
        if stat.steps > 0 {
            self.window_means.push(total / stat.steps as f64);
        }
        self.max_ms = self.max_ms.max(max);
        self.over50 += over50;
        self.step_weighted_p50 += stat.steps as f64 * stat.step_ms.p50;
    }
    fn finish(mut self) -> PhaseSummary {
        let mean_ms = (self.steps > 0).then(|| self.total_ms / self.steps as f64);
        PhaseSummary {
            windows: self.windows,
            steps: self.steps,
            total_ms: self.total_ms,
            mean_ms,
            p99_ms: percentile(&mut self.window_means, 0.99),
            max_ms: (self.windows > 0).then_some(self.max_ms),
            over50: self.over50,
            share_pct: match (mean_ms, self.steps) {
                (Some(m), s) if s > 0 && self.step_weighted_p50 > 0.0 => Some(100.0 * m / (self.step_weighted_p50 / s as f64)),
                _ => None,
            },
        }
    }
}

/// Parses tick-stats text: valid lines, and the count of non-blank lines that did not read.
pub fn parse_text(text: &str) -> (Vec<TickStat>, u64) {
    let mut out = Vec::new();
    let mut malformed = 0u64;
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        match serde_json::from_str::<TickStat>(line) {
            Ok(s) => out.push(s),
            Err(_) => malformed += 1,
        }
    }
    (out, malformed)
}

/// Summarises parsed lines under `label`.
pub fn summarize(label: &str, stats: &[TickStat], malformed: u64) -> FileSummary {
    let mut worlds: BTreeMap<WorldKey, (WorldSummary, f64)> = BTreeMap::new();
    let mut phases: BTreeMap<(WorldKey, String, bool), PhaseAcc> = BTreeMap::new(); // bool: true = Pre
    let mut tasks: Option<TasksSummary> = None;
    let mut seen_task_windows: HashSet<u64> = HashSet::new();

    for stat in stats {
        let key = WorldKey::of(stat);
        let budget = key.budget_ms();
        let (w, weighted_p50) = worlds.entry(key.clone()).or_insert_with(|| {
            (WorldSummary { budget_ms: budget, ..Default::default() }, 0.0)
        });
        w.windows += 1;
        w.steps += stat.steps;
        *weighted_p50 += stat.steps as f64 * stat.step_ms.p50;
        w.step_p99_ms = Some(w.step_p99_ms.unwrap_or(0.0).max(stat.step_ms.p99));
        w.step_max_ms = Some(w.step_max_ms.unwrap_or(0.0).max(stat.step_ms.max));
        if let Some(b) = budget {
            if stat.step_ms.max > b {
                w.windows_over_budget += 1;
            }
            w.steps_over_budget_min += steps_over_min(stat.steps, stat.step_ms.p99, stat.step_ms.max, b);
        }
        if let Some(pre) = &stat.pre_parts {
            for s in &pre.subs {
                phases.entry((key.clone(), s.owner.clone(), true)).or_insert_with(|| PhaseAcc {
                    windows: 0, steps: 0, total_ms: 0.0, window_means: Vec::new(), max_ms: 0.0, over50: 0, step_weighted_p50: 0.0,
                }).add(stat, s.total, s.max, s.over50);
            }
        }
        if let Some(post) = &stat.post_parts {
            for s in &post.subs {
                phases.entry((key.clone(), s.owner.clone(), false)).or_insert_with(|| PhaseAcc {
                    windows: 0, steps: 0, total_ms: 0.0, window_means: Vec::new(), max_ms: 0.0, over50: 0, step_weighted_p50: 0.0,
                }).add(stat, s.total, s.max, s.over50);
            }
        }
        if let Some(t) = &stat.tasks {
            // several worlds can write one pool window: count a repeated window id once
            if let Some(id) = t.window.as_ref().and_then(|w| w.id) && !seen_task_windows.insert(id) {
                continue;
            }
            let ts = tasks.get_or_insert_with(TasksSummary::default);
            ts.windows += 1;
            for o in &t.owners {
                accumulate_task_owner(ts, o);
            }
            if let Some(a) = &t.apply {
                let ap = ts.apply.get_or_insert_with(ApplySummary::default);
                ap.windows += 1;
                ap.applied += a.applied;
                ap.deferred += a.deferred;
                ap.cancelled += a.cancelled;
                ap.ms += a.ms;
                ap.owner_budget_stops += a.owner_budget_stops;
                ap.step_budget_stops += a.step_budget_stops;
                ap.backlog_max = ap.backlog_max.max(a.backlog);
            }
            if let Some(h) = &t.hops {
                ts.hops_refused += h.refused;
            }
        }
    }

    let worlds = worlds
        .into_iter()
        .map(|(k, (mut w, weighted))| {
            w.step_mean_ms = (w.steps > 0 && weighted > 0.0).then(|| weighted / w.steps as f64);
            (k, w)
        })
        .collect();
    let mut mods: BTreeMap<(WorldKey, String), ModSummary> = BTreeMap::new();
    for ((world, owner, is_pre), acc) in phases {
        let m = mods.entry((world, owner)).or_default();
        if is_pre {
            m.pre = Some(acc.finish());
        } else {
            m.post = Some(acc.finish());
        }
    }
    if let Some(ts) = &mut tasks {
        for o in ts.owners.values_mut() {
            o.cpu_ms_per_window = (o.windows > 0).then(|| o.cpu_ms / o.windows as f64);
            o.wall_ms_per_window = (o.windows > 0).then(|| o.wall_ms / o.windows as f64);
        }
        if let Some(ap) = &mut ts.apply {
            ap.ms_per_window = (ap.windows > 0).then(|| ap.ms / ap.windows as f64);
        }
    }
    FileSummary { label: label.to_string(), lines: stats.len() as u64, malformed, worlds, mods, tasks }
}

fn accumulate_task_owner(ts: &mut TasksSummary, o: &TaskOwner) {
    let e = ts.owners.entry(o.owner.clone()).or_default();
    e.windows += 1;
    e.cpu_ms += o.cpu_ms;
    e.wall_ms += o.wall_ms;
    if e.longest_ms.is_none_or(|m| o.longest_ms > m) {
        e.longest_ms = Some(o.longest_ms);
        e.longest_name = o.longest_name.clone();
    }
    e.completed += o.completed;
    e.faulted += o.faulted;
    e.cancelled += o.cancelled;
    e.queued_too_long += o.queued_too_long;
    e.running_too_long += o.running_too_long;
    e.queue_full += o.queue_full;
}

/// Reads and summarises one `tick-stats.jsonl` file (CRLF tolerated, as everywhere in `logs`).
pub fn summarize_file(path: &Path) -> Result<FileSummary> {
    let bytes = std::fs::read(path).map_err(|e| Error::from(e).with_detail(path.display().to_string()))?;
    let text = String::from_utf8_lossy(&bytes).replace('\r', "");
    let (stats, malformed) = parse_text(&text);
    Ok(summarize(&path.display().to_string(), &stats, malformed))
}

/// A before/after pair of one number: either side may be missing (a world or mod of one file only).
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Delta {
    pub before: Option<f64>,
    pub after: Option<f64>,
    pub delta: Option<f64>,
    /// `(after - before) / before * 100`; None when before is missing or 0.
    pub delta_pct: Option<f64>,
}

fn delta(before: Option<f64>, after: Option<f64>) -> Delta {
    Delta {
        before,
        after,
        delta: match (before, after) {
            (Some(b), Some(a)) => Some(a - b),
            _ => None,
        },
        delta_pct: match (before, after) {
            (Some(b), Some(a)) if b != 0.0 => Some(100.0 * (a - b) / b),
            _ => None,
        },
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorldCompare {
    pub world: WorldKey,
    pub label: String,
    pub budget_ms: Option<f64>,
    pub before: Option<WorldSummary>,
    pub after: Option<WorldSummary>,
    pub step_mean: Delta,
    pub step_p99: Delta,
    pub step_max: Delta,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PhaseCompare {
    pub before: Option<PhaseSummary>,
    pub after: Option<PhaseSummary>,
    pub mean: Delta,
    pub p99: Delta,
    pub max: Delta,
    pub share: Delta,
}

fn phase_compare(before: Option<PhaseSummary>, after: Option<PhaseSummary>) -> PhaseCompare {
    PhaseCompare {
        mean: delta(before.as_ref().and_then(|p| p.mean_ms), after.as_ref().and_then(|p| p.mean_ms)),
        p99: delta(before.as_ref().and_then(|p| p.p99_ms), after.as_ref().and_then(|p| p.p99_ms)),
        max: delta(before.as_ref().and_then(|p| p.max_ms), after.as_ref().and_then(|p| p.max_ms)),
        share: delta(before.as_ref().and_then(|p| p.share_pct), after.as_ref().and_then(|p| p.share_pct)),
        before,
        after,
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModCompare {
    pub world: WorldKey,
    pub label: String,
    pub owner: String,
    pub pre: PhaseCompare,
    pub post: PhaseCompare,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskOwnerCompare {
    pub owner: String,
    pub before: Option<TaskOwnerSummary>,
    pub after: Option<TaskOwnerSummary>,
    pub cpu_ms: Delta,
    pub wall_ms: Delta,
}

/// The `tasks` sections compared; None when neither file has one.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TasksCompare {
    pub before: Option<TasksSummary>,
    pub after: Option<TasksSummary>,
    pub owners: Vec<TaskOwnerCompare>,
    pub apply_before: Option<ApplySummary>,
    pub apply_after: Option<ApplySummary>,
}

/// The whole report: both summaries and the comparison, as `--json` prints it.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub ok: bool,
    pub before: FileSummary,
    pub after: FileSummary,
    pub worlds: Vec<WorldCompare>,
    pub mods: Vec<ModCompare>,
    pub tasks: Option<TasksCompare>,
    /// How the numbers were derived (the short form of this module's docs), so a saved report stays readable.
    pub notes: Vec<String>,
}

fn notes() -> Vec<String> {
    vec![
        "step mean: steps-weighted mean of the windows' step_ms.p50 (tick-stats holds no step total)".into(),
        "step p99: the worst window's p99; a mod's p99: p99 of its per-window means (total/steps)".into(),
        "a mod's mean is total/steps and its share is that mean over the step mean of the same windows".into(),
        "over budget: a lower bound (windows with max over the budget, and the least steps that must be over it); the file holds no per-step samples".into(),
        "budgets: 40.9 ms dedicated server, 16.8 ms hosting client's server world; other worlds have none".into(),
        "pre_parts lists only the eight slowest Pre subscribers per window: a missing Pre owner was not among them, not necessarily idle".into(),
    ]
}

/// Compares two summarised files.
pub fn compare(before: FileSummary, after: FileSummary) -> Report {
    let world_keys: std::collections::BTreeSet<&WorldKey> = before.worlds.keys().chain(after.worlds.keys()).collect();
    let worlds = world_keys
        .into_iter()
        .map(|k| {
            let b = before.worlds.get(k);
            let a = after.worlds.get(k);
            WorldCompare {
                world: k.clone(),
                label: k.label(),
                budget_ms: b.and_then(|w| w.budget_ms).or_else(|| a.and_then(|w| w.budget_ms)),
                step_mean: delta(b.and_then(|w| w.step_mean_ms), a.and_then(|w| w.step_mean_ms)),
                step_p99: delta(b.and_then(|w| w.step_p99_ms), a.and_then(|w| w.step_p99_ms)),
                step_max: delta(b.and_then(|w| w.step_max_ms), a.and_then(|w| w.step_max_ms)),
                before: b.cloned(),
                after: a.cloned(),
            }
        })
        .collect();

    let mod_keys: std::collections::BTreeSet<&(WorldKey, String)> = before.mods.keys().chain(after.mods.keys()).collect();
    let mods = mod_keys
        .into_iter()
        .map(|(world, owner)| {
            let b = before.mods.get(&(world.clone(), owner.clone()));
            let a = after.mods.get(&(world.clone(), owner.clone()));
            ModCompare {
                world: world.clone(),
                label: world.label(),
                owner: owner.clone(),
                pre: phase_compare(b.and_then(|m| m.pre.clone()), a.and_then(|m| m.pre.clone())),
                post: phase_compare(b.and_then(|m| m.post.clone()), a.and_then(|m| m.post.clone())),
            }
        })
        .collect();

    let tasks = if before.tasks.is_none() && after.tasks.is_none() {
        None
    } else {
        let owners: std::collections::BTreeSet<&String> = before
            .tasks
            .iter()
            .flat_map(|t| t.owners.keys())
            .chain(after.tasks.iter().flat_map(|t| t.owners.keys()))
            .collect();
        Some(TasksCompare {
            owners: owners
                .into_iter()
                .map(|owner| {
                    let b = before.tasks.as_ref().and_then(|t| t.owners.get(owner));
                    let a = after.tasks.as_ref().and_then(|t| t.owners.get(owner));
                    TaskOwnerCompare {
                        owner: owner.clone(),
                        cpu_ms: delta(b.map(|o| o.cpu_ms), a.map(|o| o.cpu_ms)),
                        wall_ms: delta(b.map(|o| o.wall_ms), a.map(|o| o.wall_ms)),
                        before: b.cloned(),
                        after: a.cloned(),
                    }
                })
                .collect(),
            apply_before: before.tasks.as_ref().and_then(|t| t.apply.clone()),
            apply_after: after.tasks.as_ref().and_then(|t| t.apply.clone()),
            before: before.tasks.clone(),
            after: after.tasks.clone(),
        })
    };

    Report { ok: true, before, after, worlds, mods, tasks, notes: notes() }
}

/// Compares two files by path.
pub fn compare_files(before: &Path, after: &Path) -> Result<Report> {
    Ok(compare(summarize_file(before)?, summarize_file(after)?))
}

// ---- plain text ----

fn fmt(v: Option<f64>, digits: usize) -> String {
    v.map(|x| format!("{x:.digits$}")).unwrap_or_else(|| "-".into())
}

fn fmt_pct(v: Option<f64>) -> String {
    v.map(|x| format!("{x:.2}%")).unwrap_or_else(|| "-".into())
}

fn fmt_delta(d: &Delta) -> String {
    match (d.delta, d.delta_pct) {
        (Some(v), Some(p)) => format!("{v:+.3} ({p:+.1}%)"),
        (Some(v), None) => format!("{v:+.3}"),
        _ => "-".into(),
    }
}

fn side<T>(v: &Option<T>) -> &'static str {
    if v.is_some() { "" } else { " (one file only)" }
}

/// The plain-text report (what `kilnctl tick-compare` prints without `--json`).
pub fn render_text(r: &Report) -> String {
    let mut s = String::new();
    s.push_str(&format!("tick-compare: {} -> {}\n", r.before.label, r.after.label));
    for f in [&r.before, &r.after] {
        s.push_str(&format!("  {}: {} lines, {} malformed skipped\n", f.label, f.lines, f.malformed));
        if f.lines == 0 {
            s.push_str("  warning: no valid tick-stats lines in this file\n");
        }
    }

    s.push_str("\nworlds (step time in ms; over budget is a lower bound, see notes)\n");
    if r.worlds.is_empty() {
        s.push_str("  (no windows in either file)\n");
    }
    for w in &r.worlds {
        s.push_str(&format!(
            "  {}: budget {} ms\n    step mean {} -> {} [{}], p99 {} -> {}, max {} -> {}\n",
            w.label,
            fmt(w.budget_ms, 1),
            fmt(w.step_mean.before, 3),
            fmt(w.step_mean.after, 3),
            fmt_delta(&w.step_mean),
            fmt(w.step_p99.before, 3),
            fmt(w.step_p99.after, 3),
            fmt(w.step_max.before, 3),
            fmt(w.step_max.after, 3),
        ));
        for (name, f) in [("before", &w.before), ("after", &w.after)] {
            if let Some(f) = f {
                s.push_str(&format!(
                    "    {name}: {} windows, {} steps, over budget >= {} steps in {} windows\n",
                    f.windows, f.steps, f.steps_over_budget_min, f.windows_over_budget
                ));
            } else {
                s.push_str(&format!("    {name}: (absent)\n"));
            }
        }
    }

    s.push_str("\nmods (ms per step; share = share of the step mean of the same windows)\n");
    if r.mods.is_empty() {
        s.push_str("  (no pre_parts/post_parts in either file)\n");
    }
    for m in &r.mods {
        s.push_str(&format!("  [{}] {}\n", m.label, m.owner));
        for (name, p) in [("pre", &m.pre), ("post", &m.post)] {
            if p.before.is_none() && p.after.is_none() {
                continue;
            }
            s.push_str(&format!(
                "    {name}: mean {} -> {} [{}], p99 {} -> {}, max {} -> {}, share {} -> {}{}{}\n",
                fmt(p.mean.before, 4),
                fmt(p.mean.after, 4),
                fmt_delta(&p.mean),
                fmt(p.p99.before, 4),
                fmt(p.p99.after, 4),
                fmt(p.max.before, 3),
                fmt(p.max.after, 3),
                fmt_pct(p.share.before),
                fmt_pct(p.share.after),
                side(&p.before),
                side(&p.after),
            ));
        }
    }

    match &r.tasks {
        None => s.push_str("\ntasks: no tasks section in either file (skipped)\n"),
        Some(t) => {
            s.push_str("\ntasks (per owner, summed over the windows that carry the section)\n");
            for o in &t.owners {
                s.push_str(&format!(
                    "  {}: cpu {} -> {} ms [{}], wall {} -> {} ms [{}]{}{}\n",
                    o.owner,
                    fmt(o.cpu_ms.before, 1),
                    fmt(o.cpu_ms.after, 1),
                    fmt_delta(&o.cpu_ms),
                    fmt(o.wall_ms.before, 1),
                    fmt(o.wall_ms.after, 1),
                    fmt_delta(&o.wall_ms),
                    side(&o.before),
                    side(&o.after),
                ));
                for (name, f) in [("before", &o.before), ("after", &o.after)] {
                    if let Some(f) = f {
                        s.push_str(&format!(
                            "    {name}: {} windows, longest {} ms{}, completed {}, faulted {}, cancelled {}\n",
                            f.windows,
                            fmt(f.longest_ms, 3),
                            f.longest_name.as_deref().map(|n| format!(" ({n})")).unwrap_or_default(),
                            f.completed,
                            f.faulted,
                            f.cancelled,
                        ));
                    }
                }
            }
            for (name, a) in [("before", &t.apply_before), ("after", &t.apply_after)] {
                match a {
                    Some(a) => s.push_str(&format!(
                        "  apply ({name}): {} applied, {} deferred, {} cancelled, {} ms total ({} ms/window), backlog max {}\n",
                        a.applied,
                        a.deferred,
                        a.cancelled,
                        fmt(Some(a.ms), 3),
                        fmt(a.ms_per_window, 3),
                        a.backlog_max
                    )),
                    None => s.push_str(&format!("  apply ({name}): (no tasks section)\n")),
                }
            }
        }
    }

    s.push_str("\nnotes\n");
    for n in &r.notes {
        s.push_str(&format!("  - {n}\n"));
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percentile_is_nearest_rank() {
        assert_eq!(percentile(&mut [1.0, 2.0, 3.0, 4.0], 0.5), Some(2.0));
        assert_eq!(percentile(&mut [4.0, 1.0, 3.0, 2.0], 0.99), Some(4.0));
        assert_eq!(percentile(&mut [7.0], 0.99), Some(7.0));
        assert_eq!(percentile(&mut [], 0.99), None);
        assert_eq!(mean(&[1.0, 2.0, 3.0]), Some(2.0));
    }

    #[test]
    fn over_budget_lower_bound_follows_the_native_percentile() {
        // p99 is element floor(0.99 * n): n=100 -> element 99, so exactly 1 step is >= p99
        assert_eq!(steps_over_min(100, 50.0, 60.0, 40.9), 1);
        assert_eq!(steps_over_min(1000, 50.0, 60.0, 40.9), 10);
        // only the max over: at least the one longest step
        assert_eq!(steps_over_min(1000, 10.0, 60.0, 40.9), 1);
        assert_eq!(steps_over_min(1000, 10.0, 40.0, 40.9), 0);
        assert_eq!(steps_over_min(0, 50.0, 60.0, 40.9), 0);
    }
}
