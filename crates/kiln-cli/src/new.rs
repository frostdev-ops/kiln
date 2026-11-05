// SPDX-License-Identifier: MIT
//! `kiln new`: dotnet new kiln-<kind> from Kiln.Templates (installed from the MDK feed when missing).

use crate::out::{self, CliError};
use crate::tools;
use anyhow::Result;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::process::Command;

/// The templates this kiln knows (the same files Kiln.Templates packs).
const TEMPLATES: &[&str] = &[
    include_str!("../../../templates/kiln-mod/.template.config/template.json"),
    include_str!("../../../templates/kiln-datapack/.template.config/template.json"),
    include_str!("../../../templates/kiln-items/.template.config/template.json"),
    include_str!("../../../templates/kiln-creature/.template.config/template.json"),
    include_str!("../../../templates/kiln-ability/.template.config/template.json"),
    include_str!("../../../templates/kiln-spell/.template.config/template.json"),
    include_str!("../../../templates/kiln-ui/.template.config/template.json"),
    include_str!("../../../templates/kiln-native/.template.config/template.json"),
    include_str!("../../../templates/kiln-model/.template.config/template.json"),
    include_str!("../../../templates/kiln-sound/.template.config/template.json"),
    include_str!("../../../templates/kiln-talents/.template.config/template.json"),
    include_str!("../../../templates/kiln-test/.template.config/template.json"),
    include_str!("../../../templates/kiln-service/.template.config/template.json"),
];

/// Options every template has that `kiln new` sets itself (or that are internal).
const HIDDEN: &[&str] = &["kilnSdkVersion"];

#[derive(clap::Args)]
pub struct NewArgs {
    /// Template: kiln-mod, kiln-datapack, ... (the `kiln-` may be left out: `kiln new items MyItems`).
    pub template: Option<String>,
    /// Project name (folder and assembly name).
    pub name: Option<String>,
    /// List the templates and their options.
    #[arg(long)]
    pub list: bool,
    /// Output folder (default: ./<name>).
    #[arg(long, short = 'o')]
    pub dir: Option<PathBuf>,
    /// Server (or game) folder for deploys (KilnServer).
    #[arg(long)]
    pub server: Option<PathBuf>,
    /// The MDK feed (default: $KILN_FEED, $KILN_OUT/nuget, <data>/sdk/nuget); written into the project's nuget.config.
    #[arg(long)]
    pub feed: Option<PathBuf>,
    /// A template option, repeatable: --set services=Players;Chat --set tests=true
    #[arg(long = "set", value_name = "OPTION=VALUE")]
    pub set: Vec<String>,
    /// Use this dotnet new settings folder instead of your own (keeps experiments out of the user setup).
    #[arg(long, value_name = "DIR", hide = true)]
    pub hive: Option<PathBuf>,
}

struct Template {
    id: String,
    name: String,
    description: String,
    kind: String,
    language: String,
    options: Vec<Value>,
}

fn templates() -> Vec<Template> {
    TEMPLATES
        .iter()
        .filter_map(|t| serde_json::from_str::<Value>(t).ok())
        .map(|t| {
            let mut options = Vec::new();
            if let Some(sym) = t["symbols"].as_object() {
                for (name, s) in sym {
                    if s["type"] != "parameter" || HIDDEN.contains(&name.as_str()) {
                        continue;
                    }
                    let mut o = json!({
                        "name": name,
                        "type": match s["datatype"].as_str() { Some("bool") => "bool", Some("choice") => "choice", _ => "string" },
                        "default": s.get("defaultValue").cloned().unwrap_or(Value::Null),
                        "description": s["description"],
                    });
                    if let Some(c) = s.get("choices") {
                        o["choices"] = c.clone();
                    }
                    options.push(o);
                }
            }
            Template {
                id: t["shortName"].as_str().unwrap_or("").to_string(),
                name: t["name"].as_str().unwrap_or("").to_string(),
                description: t["description"].as_str().unwrap_or("").to_string(),
                kind: t["tags"]["kiln.kind"].as_str().unwrap_or("").to_string(),
                language: t["tags"]["language"].as_str().unwrap_or("C#").to_string(),
                options,
            }
        })
        .collect()
}

pub fn run(a: NewArgs, json: bool) -> Result<i32> {
    let all = templates();
    if a.list || a.template.is_none() {
        if json {
            out::print_json(&Value::Array(
                all.iter()
                    .map(|t| json!({"id": t.id, "name": t.name, "description": t.description, "kind": t.kind,
                                    "language": t.language, "options": t.options}))
                    .collect(),
            ));
        } else {
            println!("Kiln templates (kiln new <template> <Name>):");
            for t in &all {
                println!("  {:<14} {}", t.id, t.description);
                for o in &t.options {
                    println!("      --set {}=…  {} (default {})", o["name"].as_str().unwrap_or(""), o["description"].as_str().unwrap_or(""), o["default"]);
                }
            }
        }
        return Ok(0);
    }
    let want = a.template.unwrap();
    let id = if want.starts_with("kiln-") { want.clone() } else { format!("kiln-{want}") };
    let Some(t) = all.iter().find(|t| t.id == id) else {
        return Err(CliError::usage(format!(
            "no template '{want}' (kiln new --list): {}",
            all.iter().map(|t| t.id.as_str()).collect::<Vec<_>>().join(", ")
        ))
        .into());
    };
    let Some(name) = a.name else { return Err(CliError::usage("kiln new <template> <Name>: the name is missing").into()) };
    let dir = a.dir.clone().unwrap_or_else(|| PathBuf::from(&name));
    if dir.exists() && std::fs::read_dir(&dir).map(|mut d| d.next().is_some()).unwrap_or(false) {
        return Err(CliError::new("conflict", format!("{} exists and is not empty", dir.display())).into());
    }
    let feed = tools::feed(a.feed.as_deref());
    let mut extra: Vec<String> = Vec::new();
    for s in &a.set {
        let Some((k, v)) = s.split_once('=') else { return Err(CliError::usage(format!("--set {s}: expected OPTION=VALUE")).into()) };
        if !t.options.iter().any(|o| o["name"] == k) {
            return Err(CliError::usage(format!("{}: no option '{k}' (kiln new --list)", t.id)).into());
        }
        extra.push(format!("--{k}"));
        extra.push(v.to_string());
    }
    if let Some(s) = &a.server {
        let s = std::path::absolute(s).unwrap_or(s.clone());
        if t.options.iter().any(|o| o["name"] == "server") {
            extra.push("--server".into());
            extra.push(s.display().to_string());
        }
    }
    if let Some(f) = &feed {
        if t.options.iter().any(|o| o["name"] == "feed") {
            extra.push("--feed".into());
            extra.push(std::path::absolute(f).unwrap_or(f.clone()).display().to_string());
        }
    }
    let hive: Vec<String> = a.hive.iter().flat_map(|h| ["--debug:custom-hive".to_string(), h.display().to_string()]).collect();
    let attempt = |quiet: bool| -> Result<std::process::Output> {
        let mut cmd = Command::new("dotnet");
        cmd.arg("new").arg(&t.id).arg("-n").arg(&name).arg("-o").arg(&dir).args(&extra).args(&hive);
        let o = cmd.output().map_err(|e| CliError::new("tool_missing", format!("cannot run dotnet: {e}")))?;
        if !quiet && !o.status.success() {
            eprint!("{}", String::from_utf8_lossy(&o.stdout));
            eprint!("{}", String::from_utf8_lossy(&o.stderr));
        }
        Ok(o)
    };
    let mut o = attempt(true)?;
    let missing = |o: &std::process::Output| {
        let s = format!("{}{}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr));
        s.contains("No templates or subcommands found") || s.contains("No templates found") || s.contains("is not a valid")
    };
    if !o.status.success() && missing(&o) {
        install_templates(feed.as_deref(), &hive, json)?;
        o = attempt(false)?;
    } else if !o.status.success() {
        eprint!("{}{}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr));
    }
    if !o.status.success() {
        return Err(CliError::new("template_failed", format!("dotnet new {} failed", t.id)).into());
    }
    let dir = std::path::absolute(&dir).unwrap_or(dir);
    let project = std::fs::read_dir(&dir)
        .ok()
        .and_then(|d| d.flatten().map(|e| e.path()).find(|p| p.extension().is_some_and(|x| x == "csproj")));
    if json {
        out::print_json(&json!({"ok": true, "template": t.id, "dir": dir, "project": project}));
    } else {
        println!("{} created in {}", t.id, dir.display());
        println!("next: cd {} && kiln build && kiln deploy{}", dir.display(), if a.server.is_none() { " --server <folder>" } else { "" });
    }
    Ok(0)
}

fn install_templates(feed: Option<&Path>, hive: &[String], json: bool) -> Result<()> {
    let v = tools::sdk_version();
    let Some(feed) = feed else {
        return Err(CliError::new("not_found", "Kiln.Templates is not installed and no MDK feed is known (--feed, KILN_FEED)").into());
    };
    out::say(json, format!("installing Kiln.Templates {v} from {}", feed.display()));
    let nupkg = feed.join(format!("Kiln.Templates.{v}.nupkg"));
    let mut cmd = Command::new("dotnet");
    cmd.arg("new").arg("install");
    if nupkg.is_file() {
        cmd.arg(&nupkg);
    } else {
        cmd.arg(format!("Kiln.Templates::{v}")).arg("--add-source").arg(feed);
    }
    cmd.args(hive);
    let s = tools::run_passthrough(&mut cmd, json)?;
    if !s.success() {
        return Err(CliError::new("template_failed", "dotnet new install Kiln.Templates failed").into());
    }
    Ok(())
}
