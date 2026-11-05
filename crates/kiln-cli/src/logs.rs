// SPDX-License-Identifier: MIT
//! `kiln logs`: kiln/logs/kiln.log of a server or game, filtered by mod and level; --follow keeps reading.

use crate::out::CliError;
use crate::project::Project;
use crate::server;
use anyhow::Result;
use serde_json::json;
use std::time::Duration;

#[derive(clap::Args)]
pub struct LogsArgs {
    #[arg(long)]
    pub server: Option<String>,
    /// Keep printing new lines (Ctrl-C to stop).
    #[arg(long, short = 'f')]
    pub follow: bool,
    /// Only this mod's lines (`[mod:<id>]`, its native mod and service lines that name it).
    #[arg(long = "mod", value_name = "ID")]
    pub mod_id: Option<String>,
    /// Lowest level: D, I, W or E.
    #[arg(long, value_name = "LEVEL")]
    pub level: Option<String>,
    /// How many of the last lines to start with.
    #[arg(long, short = 'n', default_value_t = 50)]
    pub lines: usize,
    /// Another log of the folder: kiln.log (default), game, proxy, mods.txt.
    #[arg(long, default_value = "kiln")]
    pub file: String,
}

fn rank(c: char) -> u8 {
    match c {
        'D' => 0,
        'I' => 1,
        'W' => 2,
        'E' => 3,
        _ => 1,
    }
}

pub fn run(a: LogsArgs, json: bool) -> Result<i32> {
    let project = Project::find(None).ok();
    let srv = server::resolve(a.server.as_deref(), project.as_ref())?;
    let path = match a.file.as_str() {
        "kiln" | "kiln.log" => srv.dir.join("kiln/logs/kiln.log"),
        "proxy" | "proxy.log" => srv.dir.join("kiln/logs/proxy.log"),
        "mods" | "mods.txt" => srv.dir.join("kiln/logs/mods.txt"),
        "game" => srv.dir.join("logs/enshrouded_server.log"),
        other => srv.dir.join(other),
    };
    if !path.is_file() && !a.follow {
        return Err(CliError::new("not_found", format!("{} does not exist yet", path.display())).into());
    }
    let min = a.level.as_deref().and_then(|l| l.chars().next()).map(|c| rank(c.to_ascii_uppercase())).unwrap_or(0);
    let tag = a.mod_id.as_ref().map(|id| format!("mod:{id}"));
    let keep = |line: &str| -> Option<serde_json::Value> {
        match kiln_manage::logs::parse_line(line) {
            Some(l) => {
                if rank(l.level) < min {
                    return None;
                }
                if let (Some(t), Some(id)) = (&tag, &a.mod_id) {
                    let mine = &l.source == t || l.source == format!("native:{id}") || l.message.contains(&format!("mod {id} "));
                    if !mine {
                        return None;
                    }
                }
                Some(json!({"t": l.time, "level": l.level.to_string(), "tag": l.source, "text": l.message}))
            }
            None if tag.is_none() && min == 0 => Some(json!({"t": null, "level": null, "tag": null, "text": line})),
            None => None,
        }
    };
    let emit = |line: &str| {
        if let Some(v) = keep(line) {
            if json {
                println!("{v}");
            } else {
                println!("{line}");
            }
        }
    };
    let text = std::fs::read(&path).map(|b| String::from_utf8_lossy(&b).replace('\r', "")).unwrap_or_default();
    let all: Vec<&str> = text.lines().filter(|l| keep(l).is_some()).collect();
    for l in &all[all.len().saturating_sub(a.lines)..] {
        emit(l);
    }
    if !a.follow {
        return Ok(0);
    }
    let mut f = kiln_manage::logs::Follower::open(&path)?;
    loop {
        for l in f.poll()? {
            emit(&l);
        }
        std::thread::sleep(Duration::from_millis(250));
    }
}
