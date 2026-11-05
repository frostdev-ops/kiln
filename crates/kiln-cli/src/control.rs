// SPDX-License-Identifier: MIT
//! `kiln reload` and `kiln test`: the running server's control folder (kiln/control/<name>.cmd -> .result.json) and
//! Kiln.Testing's report (kiln/logs/tests/<id>.json).

use crate::out::{self, CliError};
use crate::project::Project;
use crate::server;
use anyhow::Result;
use serde_json::{Value, json};
use std::time::Duration;

#[derive(clap::Args)]
pub struct ReloadArgs {
    /// Mod id (default: the current project's KilnModId).
    pub id: Option<String>,
    #[arg(long)]
    pub server: Option<String>,
    /// Seconds to wait for the server's answer.
    #[arg(long, default_value_t = 60)]
    pub timeout: u64,
}

#[derive(clap::Args)]
pub struct TestArgs {
    /// Mod id (default: the current project's KilnModId).
    pub id: Option<String>,
    /// Only tests whose name contains this (case-insensitive; names are `Class.Method` or [KilnTest(Name=...)]).
    pub filter: Option<String>,
    /// Run exactly the test with this name.
    #[arg(long, conflicts_with = "filter")]
    pub exact: Option<String>,
    #[arg(long)]
    pub server: Option<String>,
    /// Seconds to wait for the run (Kiln's own per-test timeouts apply inside it).
    #[arg(long, default_value_t = 300)]
    pub timeout: u64,
}

fn mod_id(arg: Option<String>, project: Option<&Project>) -> Result<String> {
    if let Some(id) = arg {
        return Ok(id);
    }
    if let Some(p) = project.filter(|p| p.kind == crate::project::Kind::Dotnet) {
        let id = p.property("KilnModId")?;
        if !id.is_empty() {
            return Ok(id);
        }
    }
    Err(CliError::usage("which mod? give its id (or run inside its project)").into())
}

pub(crate) fn send(dir: &std::path::Path, command: &str, timeout: u64) -> Result<Value> {
    match kiln_manage::control::send(dir, command, Duration::from_secs(timeout)) {
        Ok(v) => Ok(v),
        Err(e) if e.code == kiln_manage::ErrorCode::Timeout => {
            let picked = e.detail.as_ref().and_then(|d| d["picked"].as_bool()).unwrap_or(false);
            Err(CliError::new(
                if picked { "timeout" } else { "not_running" },
                if picked {
                    format!("the server took the command but did not answer within {timeout} s")
                } else {
                    format!("no answer: is the server in {} running with Kiln? (the control folder is polled from world steps)", dir.display())
                },
            )
            .into())
        }
        Err(e) => Err(e.into()),
    }
}

pub fn reload(a: ReloadArgs, json: bool) -> Result<i32> {
    let project = Project::find(None).ok();
    let id = mod_id(a.id, project.as_ref())?;
    let srv = server::resolve(a.server.as_deref(), project.as_ref())?;
    let r = send(&srv.dir, &format!("reload {id}"), a.timeout)?;
    let ok = r["ok"].as_bool().unwrap_or(false);
    if json {
        out::print_json(&r);
    } else {
        println!("reload {id}: {}", r["message"].as_str().unwrap_or(if ok { "ok" } else { "failed" }));
    }
    Ok(if ok { 0 } else { 1 })
}

pub fn test(a: TestArgs, json: bool) -> Result<i32> {
    let project = Project::find(None).ok();
    let id = mod_id(a.id, project.as_ref())?;
    let srv = server::resolve(a.server.as_deref(), project.as_ref())?;
    // Kiln.Testing filters by substring; for --exact send the name and keep only that result.
    let filter = a.exact.clone().or(a.filter.clone());
    if filter.as_deref().is_some_and(|f| f.contains(char::is_whitespace)) {
        return Err(CliError::usage("a test filter cannot contain spaces").into());
    }
    let command = match &filter {
        Some(f) => format!("test {id} {f}"),
        None => format!("test {id}"),
    };
    let r = match send(&srv.dir, &command, a.timeout) {
        Ok(r) => r,
        Err(e) => {
            if let Some(c) = e.downcast_ref::<CliError>() {
                if json {
                    out::print_json(&json!({"ok": false, "error": {"code": c.code, "message": c.message}}));
                    return Ok(1);
                }
            }
            return Err(e);
        }
    };
    if r["ok"] == false && r["data"].is_null() {
        // refused before running (unknown or unloaded mod, a run in progress)
        let msg = r["message"].as_str().unwrap_or("the server refused the test run").to_string();
        let code = if msg.contains("not loaded") || msg.contains("no mod") { "not_loaded" } else { "refused" };
        if json {
            out::print_json(&json!({"ok": false, "error": {"code": code, "message": msg}}));
        } else {
            eprintln!("kiln: {msg}");
        }
        return Ok(1);
    }
    let file = srv.dir.join("kiln/logs/tests").join(format!("{id}.json"));
    let mut report: Value = std::fs::read_to_string(&file).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_else(|| {
        // fall back to the control result's summary
        let d = &r["data"];
        json!({"mod": id, "ok": r["ok"], "passed": d["passed"], "failed": d["failed"], "skipped": d["skipped"], "results": d["results"]})
    });
    if let Some(name) = &a.exact {
        if let Some(results) = report["results"].as_array().cloned() {
            let kept: Vec<Value> = results.into_iter().filter(|x| x["name"].as_str().is_some_and(|n| n.eq_ignore_ascii_case(name))).collect();
            let count = |o: &str| kept.iter().filter(|x| x["outcome"] == o).count();
            report["passed"] = json!(count("pass"));
            report["failed"] = json!(count("fail") + count("timeout"));
            report["skipped"] = json!(count("skip"));
            report["ok"] = json!(!kept.is_empty() && count("fail") + count("timeout") == 0);
            report["results"] = Value::Array(kept);
            report["filteredBy"] = json!("cli");
        }
    }
    let ok = report["ok"].as_bool().unwrap_or(false);
    if json {
        out::print_json(&report);
    } else {
        for t in report["results"].as_array().into_iter().flatten() {
            let outcome = t["outcome"].as_str().unwrap_or("?").to_uppercase();
            let msg = t["message"].as_str().filter(|m| !m.is_empty()).map(|m| format!(": {m}")).unwrap_or_default();
            println!("{outcome:<7} {} ({:.1} ms){msg}", t["name"].as_str().unwrap_or("?"), t["ms"].as_f64().unwrap_or(0.0));
        }
        println!(
            "{id}: {} passed, {} failed, {} skipped{}",
            report["passed"], report["failed"], report["skipped"],
            if file.is_file() { format!(" ({})", file.display()) } else { String::new() }
        );
    }
    Ok(if ok { 0 } else { 1 })
}
