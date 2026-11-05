// SPDX-License-Identifier: MIT
//! `kilnctl cmd`: runs a command line on a running server through Kiln's console folder
//! (`kiln/control/console/<name>.cmd` -> `<name>.result.json`, handled by Kiln.Chat on the tick thread).
//! The exchange itself is kiln_manage::control.
use crate::config;
use crate::util::Km;
use anyhow::{Context, Result, bail};
use std::path::{Path, PathBuf};
use std::time::Duration;

pub struct CmdArgs {
    pub target: String,
    pub line: String,
    pub source: Option<String>,
    pub complete: bool,
    pub json: bool,
    pub timeout: Duration,
}

/// The server folder: an instance name, or a path to a server folder.
fn server_dir(target: &str) -> Result<PathBuf> {
    let p = Path::new(target);
    if (target.contains('/') || target.starts_with('.')) && p.is_dir() {
        return Ok(p.to_path_buf());
    }
    Ok(config::load(target)?.dir)
}

pub fn run(a: CmdArgs) -> Result<i32> {
    let dir = server_dir(&a.target)?;
    let kiln = dir.join("kiln");
    if !kiln.is_dir() {
        bail!("{} has no kiln/ folder (deploy Kiln first)", dir.display());
    }
    let text = kiln_manage::control::console_text(&a.line, a.source.as_deref(), a.complete).km()?;
    let raw = match kiln_manage::control::console(&dir, &text, a.timeout) {
        Ok(raw) => raw,
        Err(e) if e.code == kiln_manage::ErrorCode::Timeout => {
            let picked = e.detail.as_ref().and_then(|d| d["picked"].as_bool()).unwrap_or(false);
            eprintln!(
                "kilnctl: no answer within {} s{} (is the server running with Kiln, Kiln.Chat and at least one mod loaded?)",
                a.timeout.as_secs(),
                if picked { " although the file was picked up" } else { "" }
            );
            return Ok(2);
        }
        Err(e) => return Err(anyhow::Error::msg(e.message)),
    };
    if a.json {
        println!("{}", raw.trim_end());
    }
    let doc: serde_json::Value = serde_json::from_str(&raw).context("the result is not JSON")?;
    let ok = doc.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
    if !a.json {
        for r in doc.get("results").and_then(|v| v.as_array()).into_iter().flatten() {
            for o in r.get("output").and_then(|v| v.as_array()).into_iter().flatten() {
                println!("{}", o.as_str().unwrap_or_default());
            }
            if let Some(c) = r.get("completions").and_then(|v| v.as_array()) {
                for s in c {
                    println!("{}", s.as_str().unwrap_or_default());
                }
            }
            if let Some(e) = r.get("error").and_then(|v| v.as_str()) {
                eprintln!("error: {e}");
            }
        }
    }
    Ok(if ok { 0 } else { 1 })
}
