// SPDX-License-Identifier: MIT
//! `kiln lint`: kiln_manage::lint. Human output is one line per finding,
//! `<path>:<line>:<col>: <severity> <code>: <message>` (a problem matcher reads it), then a summary line.

use crate::LintArgs;
use crate::out::CliError;
use anyhow::Result;
use kiln_manage::lint::{self, LintOptions, Severity};
use std::io::Read;

pub fn run(a: LintArgs, json: bool) -> Result<i32> {
    if a.list_rules {
        let rules: Vec<serde_json::Value> = lint::rules()
            .iter()
            .map(|r| serde_json::json!({"code": r.code, "name": r.name, "severity": r.severity.as_str(), "title": r.title}))
            .collect();
        if json {
            crate::out::print_json(&serde_json::Value::Array(rules));
        } else {
            for r in &rules {
                println!("{} {:<8} {:<28} {}", r["code"].as_str().unwrap_or(""), r["severity"].as_str().unwrap_or(""), r["name"].as_str().unwrap_or(""), r["title"].as_str().unwrap_or(""));
            }
        }
        return Ok(0);
    }
    let mut opts = LintOptions { catalog: a.catalog.clone(), profile: a.profile.clone(), rules_off: a.off.clone(), ..Default::default() };
    if let Some(s) = &a.min_severity {
        opts.min_severity = Severity::parse(s).ok_or_else(|| CliError::usage(format!("--min-severity {s}: error | warning | info | hint")))?;
    }
    let report = if a.stdin {
        let Some(path) = a.stdin_path.clone() else { return Err(CliError::usage("--stdin needs --stdin-path <file name>").into()) };
        let mut text = String::new();
        std::io::stdin().read_to_string(&mut text)?;
        lint::lint_text(&path, &text, &opts)
    } else {
        let paths = if a.paths.is_empty() { vec![std::env::current_dir()?] } else { a.paths.clone() };
        lint::lint_paths(&paths, &opts)
    };
    if json {
        println!("{}", report.to_json());
    } else {
        for d in &report.diagnostics {
            println!("{}:{}:{}: {} {}: {}", d.file, d.line, d.col, d.severity.as_str(), d.code, d.message);
        }
        let s = &report.summary;
        println!(
            "kiln lint: {} error(s), {} warning(s), {} info(s) in {} file(s){}",
            s.errors,
            s.warnings,
            s.infos,
            report.checked.len(),
            report.catalog.as_ref().map(|c| format!("; catalog {}", c.path)).unwrap_or_else(|| "; no game-data catalog (kiln catalog build)".into())
        );
    }
    Ok(report.exit_code())
}
