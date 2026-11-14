// SPDX-License-Identifier: MIT
//! `kiln boot-snapshot --game <dir> [--force]`: makes the game-data snapshot `Mod.OnBoot` reads (kiln_manage::boot_snapshot,
//! docs/kiln/modding/boot-snapshot.md) for a game folder: a dedicated server's or the client's. The launcher does the
//! same at Play; this is for a server started by hand, a lab, or to make it again (`--force`). Once per game build:
//! about a minute and about 200 MB the first time, well under a second when it is there already.
//!
//! Exit 0 when the snapshot is in place (made or current), also when some kind could not be read (a warning names it);
//! 1 when there is none (the reason is printed). `--json`: the result as one document.
use std::path::PathBuf;

use anyhow::Result;
use kiln_manage::boot_snapshot;

use crate::out;

#[derive(clap::Args)]
pub struct BootSnapshotArgs {
    /// The game folder: a dedicated server's (enshrouded_server.exe) or the client's (enshrouded.exe).
    #[arg(long)]
    pub game: PathBuf,
    /// Make it again even when the snapshot of this game build is there.
    #[arg(long)]
    pub force: bool,
}

pub fn run(a: BootSnapshotArgs, json: bool) -> Result<i32> {
    let game = std::path::absolute(&a.game).unwrap_or(a.game.clone());
    let tools = crate::tools::cook_tools().map_err(|e| e.to_string());
    // a terminal: no time limit (Ctrl+C stops it); the tool's progress lines go to stderr
    let r = boot_snapshot::make_with(tools, &game, a.force, None, &mut |l| {
        if !l.starts_with("warning:") {  // the result's own warning is printed below, once
            eprintln!("boot-snapshot: {l}");
        }
    }, &|| false);
    if json {
        out::print_json(&serde_json::to_value(&r)?);
    } else {
        match r.status.as_str() {
            "made" => out::say(json, format!("{} in {}", r.summary(), r.path.as_deref().unwrap_or("?"))),
            "reused" => out::say(json, format!("{} ({})", r.summary(), r.path.as_deref().unwrap_or("?"))),
            _ => {}
        }
    }
    if let Some(w) = r.warning() {
        eprintln!("warning: {w}");
    }
    Ok(if r.ready() { 0 } else { 1 })
}
