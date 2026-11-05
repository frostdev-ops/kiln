// SPDX-License-Identifier: MIT
//! `kiln blocks`: what a server's mods' building blocks became. Kiln.Blocks compiles blocks when they load and writes each mod's result to
//! kiln/data/blocks/<mod>.json: the items, recipes, templates, buffs and rules, the generated Impact programs as listings, and what went wrong.

use crate::out::CliError;
use crate::project::Project;
use crate::server;
use anyhow::Result;
use serde_json::{Value, json};
use std::path::Path;

#[derive(clap::Args)]
pub struct BlocksArgs {
    #[command(subcommand)]
    pub cmd: BlocksCmd,
    #[arg(long, global = true)]
    pub server: Option<String>,
}

#[derive(clap::Subcommand)]
pub enum BlocksCmd {
    /// The mods with blocks, each with its block names, errors and warnings.
    List {
        /// Only this mod.
        #[arg(long = "mod", value_name = "ID")]
        mod_id: Option<String>,
    },
    /// What one block became: `name`, or `mod:name` when several mods have that name.
    Show {
        name: String,
        /// Also print its programs as listings.
        #[arg(long)]
        programs: bool,
    },
}

fn reports(dir: &Path) -> Result<Vec<Value>> {
    let folder = dir.join("kiln/data/blocks");
    let mut out = vec![];
    let Ok(entries) = std::fs::read_dir(&folder) else {
        return Err(CliError::new("not_found", format!("{} does not exist: no mod with blocks has run on this server yet", folder.display())).into());
    };
    for e in entries.flatten() {
        let p = e.path();
        if p.extension().and_then(|x| x.to_str()) != Some("json") {
            continue;
        }
        let text = std::fs::read_to_string(&p)?;
        out.push(serde_json::from_str::<Value>(&text).map_err(|x| CliError::new("bad_report", format!("{}: {x}", p.display())))?);
    }
    out.sort_by(|a, b| a["mod"].as_str().cmp(&b["mod"].as_str()));
    Ok(out)
}

fn texts(v: &Value) -> Vec<&str> {
    v.as_array().map(|a| a.iter().filter_map(Value::as_str).collect()).unwrap_or_default()
}

pub fn run(a: BlocksArgs, json_out: bool) -> Result<i32> {
    let project = Project::find(None).ok();
    let srv = server::resolve(a.server.as_deref(), project.as_ref())?;
    let all = reports(&srv.dir)?;
    match a.cmd {
        BlocksCmd::List { mod_id } => {
            let mods: Vec<&Value> = all.iter().filter(|r| mod_id.as_deref().is_none_or(|m| r["mod"].as_str() == Some(m))).collect();
            if json_out {
                let rows: Vec<Value> = mods
                    .iter()
                    .map(|r| {
                        let names: Vec<&String> = r["blocks"].as_object().map(|o| o.keys().collect()).unwrap_or_default();
                        json!({"mod": r["mod"], "blocks": names, "errors": r["errors"], "warnings": r["warnings"]})
                    })
                    .collect();
                println!("{}", json!({"ok": true, "mods": rows}));
                return Ok(0);
            }
            let mut bad = 0;
            for r in mods {
                let names: Vec<&str> = r["blocks"].as_object().map(|o| o.keys().map(String::as_str).collect()).unwrap_or_default();
                println!("{}: {} block(s): {}", r["mod"].as_str().unwrap_or("?"), names.len(), names.join(", "));
                for e in texts(&r["errors"]) {
                    println!("  error: {e}");
                    bad += 1;
                }
                for w in texts(&r["warnings"]) {
                    println!("  warning: {w}");
                }
            }
            Ok(if bad > 0 { 1 } else { 0 })
        }
        BlocksCmd::Show { name, programs } => {
            let (want_mod, want) = match name.split_once(':') {
                Some((m, n)) => (Some(m), n),
                None => (None, name.as_str()),
            };
            let hits: Vec<&Value> = all.iter().filter(|r| want_mod.is_none_or(|m| r["mod"].as_str() == Some(m)) && r["blocks"].get(want).is_some()).collect();
            let [hit] = hits.as_slice() else {
                if hits.is_empty() {
                    return Err(CliError::new("not_found", format!("no block {name}: `kiln blocks list` shows what there is")).into());
                }
                let mods: Vec<&str> = hits.iter().filter_map(|r| r["mod"].as_str()).collect();
                return Err(CliError::usage(format!("several mods have {name}: say {}", mods.iter().map(|m| format!("{m}:{want}")).collect::<Vec<_>>().join(" or "))).into());
            };
            let block = &hit["blocks"][want];
            let listings: Vec<(&str, &str, &str)> = block["programs"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|p| p["guid"].as_str())
                .filter_map(|g| hit["programs"].get(g).map(|x| (g, x["key"].as_str().unwrap_or("?"), x["listing"].as_str().unwrap_or(""))))
                .collect();
            if json_out {
                let progs: Vec<Value> = listings.iter().map(|(g, k, l)| json!({"guid": g, "key": k, "listing": l})).collect();
                println!("{}", json!({"ok": true, "mod": hit["mod"], "name": want, "block": block, "programs": if programs { Value::Array(progs) } else { Value::Null }}));
                return Ok(0);
            }
            println!("{}", serde_json::to_string_pretty(block)?);
            if programs {
                for (g, k, l) in listings {
                    println!("\nprogram {k} ({g}):\n{l}");
                }
            }
            Ok(0)
        }
    }
}
