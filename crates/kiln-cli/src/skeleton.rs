// SPDX-License-Identifier: MIT
//! `kiln skeleton player OUT.glb`: the player's skeleton, read from the installed game, as a glTF to weight a worn model against, with a body in it to model around. The file holds the
//! game's data: it stays on your machine (tools/modelcook, docs/kiln/modding/cosmetics.md).

use crate::out::{self, CliError};
use crate::tools;
use anyhow::Result;
use std::path::PathBuf;

#[derive(clap::Args)]
pub struct SkeletonArgs {
    /// Whose skeleton: player (the one skeleton of both bodies).
    pub which: String,
    /// The glTF to write: .glb or .gltf.
    pub out: PathBuf,
    /// The body in the file: male, female, or none. A cook leaves it out (its node is named reference_body).
    #[arg(long, default_value = "male")]
    pub body: String,
    /// The installed game (default: KILN_GAME, else the Steam client found).
    #[arg(long)]
    pub game: Option<PathBuf>,
}

pub fn run(a: SkeletonArgs, json: bool) -> Result<i32> {
    if a.which != "player" {
        return Err(CliError::usage(format!("\"{}\": the skeleton is player", a.which)).into());
    }
    if !matches!(a.body.as_str(), "male" | "female" | "none") {
        return Err(CliError::usage(format!("--body {}: male, female or none", a.body)).into());
    }
    let cook = tools::cook_tools()?;
    let game = a.game.or_else(tools::client_dir).ok_or_else(|| CliError::new("not_found", "the skeleton is read from your installed game; none found (--game or KILN_GAME)"))?;
    let mut cmd = cook.script_command("tools/modelcook/modelcook.py");
    cmd.arg("--game").arg(&game).arg("skeleton").arg(&a.out).arg("--body").arg(&a.body);
    let ok = tools::run_passthrough(&mut cmd, json)?.success();
    if json {
        out::print_json(&serde_json::json!({"ok": ok, "file": a.out}));
    } else if ok {
        out::say(json, format!("kiln: {} written: weight to its j<index> bones, model around reference_body; keep it out of your mod's repository", a.out.display()));
    }
    Ok(if ok { 0 } else { 1 })
}
