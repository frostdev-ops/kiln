// SPDX-License-Identifier: MIT
//! `kiln vfx info`: one game VFX from the local catalog: what finds it, how long it runs, what it takes.

use crate::out::{self, CliError};
use anyhow::Result;
use kiln_manage::catalog::{self, Entry};
use serde_json::{Value, json};

#[derive(clap::Args)]
pub struct VfxArgs {
    #[command(subcommand)]
    pub cmd: VfxCmd,
}

#[derive(clap::Subcommand)]
pub enum VfxCmd {
    /// One game VFX: any name that finds it, or a GUID; "game:" may be written in front.
    Info { name: String },
}

fn names_of(e: &Entry, key: &str) -> Vec<String> {
    e.get(key).and_then(Value::as_array).into_iter().flatten().filter_map(|v| v.as_str().map(str::to_string)).collect()
}

/// The entry a name or GUID means, the way `VfxId.Game` would find it.
fn find<'a>(entries: &'a [Entry], name: &str) -> Option<&'a Entry> {
    let name = name.strip_prefix("game:").unwrap_or(name);
    let lower = name.to_ascii_lowercase();
    entries
        .iter()
        .find(|e| e.name.as_deref() == Some(name) || e.guid.as_deref() == Some(lower.as_str()))
        .or_else(|| entries.iter().find(|e| names_of(e, "names").iter().any(|n| n == name)))
}

pub fn run(a: VfxArgs, json_out: bool) -> Result<i32> {
    let VfxCmd::Info { name } = a.cmd;
    let path = catalog::find(Some("client"), None).ok_or_else(|| CliError::new("not_found", "no client catalog yet: kiln catalog build"))?;
    let cat = catalog::Catalog::load(&path)?;
    let entries = cat.section("vfx");
    if entries.is_empty() {
        return Err(CliError::new("not_found", format!("{} has no vfx section: kiln catalog build --force", path.display())).into());
    }
    let Some(e) = find(entries, &name) else {
        let hits: Vec<Value> = catalog::search(&cat, name.strip_prefix("game:").unwrap_or(&name), Some("vfx"), 5).into_iter().filter_map(|h| h.name.map(Value::from)).collect();
        return Err(CliError::new("not_found", format!("no game VFX \"{name}\" in the catalog (kiln catalog search <words> --section vfx)")).with_detail(json!({"similar": hits})).into());
    };
    if json_out {
        out::print_json(&serde_json::to_value(e)?);
        return Ok(0);
    }
    let by_name = e.get("byName").and_then(Value::as_bool) == Some(true);
    println!("{}", e.name.as_deref().unwrap_or("-"));
    println!("  guid       {}", e.guid.as_deref().unwrap_or("-"));
    println!("  write      {}", if by_name { format!("\"game:{}\"", e.name.as_deref().unwrap_or("")) } else { format!("\"game:{}\" (nothing finds it by this name at run time)", e.guid.as_deref().unwrap_or("")) });
    if let Some(s) = e.get("length").and_then(Value::as_f64) {
        println!("  runs       {s} s, played once from code");
    }
    for k in ["particles", "emitters", "meshes", "textures"] {
        if let Some(n) = e.get(k).and_then(Value::as_u64) {
            println!("  {k:<10} {n}");
        }
    }
    let params = e.get("parameters").and_then(Value::as_array).cloned().unwrap_or_default();
    if !params.is_empty() {
        println!("  parameters");
        for p in &params {
            let id = p["name"].as_str().map(str::to_string).unwrap_or_else(|| format!("{} (name not known)", p["hash"].as_str().unwrap_or("?")));
            let def = if p["default"].is_null() { String::new() } else { format!(" = {}", p["default"]) };
            println!("    {id}: {}{def}", p["type"].as_str().unwrap_or("?"));
        }
    }
    for (k, title) in [("names", "also found as"), ("usedBy", "also used by")] {
        let list: Vec<String> = names_of(e, k).into_iter().filter(|n| Some(n.as_str()) != e.name.as_deref()).collect();
        if !list.is_empty() {
            println!("  {title}: {}", list.join(", "));
        }
    }
    Ok(0)
}
