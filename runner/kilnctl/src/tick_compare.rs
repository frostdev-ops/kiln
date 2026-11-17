// SPDX-License-Identifier: MIT
//! `kilnctl tick-compare BEFORE AFTER [--json]`: the before/after tick-cost report of two `tick-stats.jsonl`
//! files (issue #57, T12). All of the work is `kiln_manage::tick_compare`; this only takes the two paths, prints
//! the text report or the one JSON document, and maps a missing file to exit 1. Reads only, takes no instance,
//! and holds no data-folder lock (see `data_use` in main.rs). The method is in docs/kiln/kilnctl.md,
//! "Measuring a task change".

use anyhow::Result;
use std::path::PathBuf;

pub struct TickCompareArgs {
    pub before: PathBuf,
    pub after: PathBuf,
    pub json: bool,
}

pub fn run(a: TickCompareArgs) -> Result<i32> {
    let report = kiln_manage::tick_compare::compare_files(&a.before, &a.after)?;
    if a.json {
        println!("{}", serde_json::to_string_pretty(&report)?);
    } else {
        print!("{}", kiln_manage::tick_compare::render_text(&report));
    }
    Ok(0)
}
