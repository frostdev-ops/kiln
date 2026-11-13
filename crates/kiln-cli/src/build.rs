// SPDX-License-Identifier: MIT
//! `kiln build | cook | deploy | pack`: Kiln.Sdk's MSBuild targets for C# mods, CMake + kiln_plugin for native mods,
//! the repository's cook tools for recipes.

use crate::out::{self, CliError};
use crate::project::{Kind, Project};
use crate::{server, tools};
use anyhow::Result;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(clap::Args)]
pub struct BuildArgs {
    /// A .csproj, a native mod folder, or a folder holding one (default: the current folder).
    pub project: Option<PathBuf>,
    /// Debug (default) or Release.
    #[arg(short = 'c', long = "configuration")]
    pub config: Option<String>,
    /// Do not run `kiln cook` first.
    #[arg(long)]
    pub no_cook: bool,
    /// Extra arguments for dotnet build / cmake --build (after --).
    #[arg(last = true)]
    pub extra: Vec<String>,
}

#[derive(clap::Args)]
pub struct CookArgs {
    /// The project, or an installed mod's folder (with its kiln.mod.json); default: the current folder.
    pub project: Option<PathBuf>,
    /// Only say whether a cook is needed (exit 1 when it is); never reads the game.
    #[arg(long)]
    pub check: bool,
    /// The game folder the tools read (default: $KILN_GAME, else the installed client). A dedicated server's folder
    /// cooks what a server uses and skips the models, which only a game client draws.
    #[arg(long)]
    pub game: Option<PathBuf>,
}

#[derive(clap::Args)]
pub struct DeployArgs {
    pub project: Option<PathBuf>,
    /// Server or game folder with Kiln, a lab name, or a server instance id.
    #[arg(long)]
    pub server: Option<String>,
    /// Hot-reload the mod in the running server afterwards (C# mods; native mods load once per start).
    #[arg(long, short = 'r')]
    pub reload: bool,
    #[arg(short = 'c', long = "configuration")]
    pub config: Option<String>,
    #[arg(last = true)]
    pub extra: Vec<String>,
}

#[derive(clap::Args)]
pub struct PackArgs {
    pub project: Option<PathBuf>,
    #[arg(short = 'c', long = "configuration")]
    pub config: Option<String>,
    /// Where to write the zip (KilnPackageOutputPath).
    #[arg(long, short = 'o')]
    pub output: Option<PathBuf>,
}

fn config_args(c: &Option<String>) -> Vec<String> {
    c.iter().flat_map(|c| ["-c".to_string(), c.clone()]).collect()
}

pub fn build(a: BuildArgs, json: bool) -> Result<i32> {
    let p = Project::find(a.project.as_deref())?;
    if !a.no_cook {
        cook_project(&p, false, None, json, true)?;
    }
    let ok = match p.kind {
        Kind::Dotnet => {
            let mut cmd = Command::new("dotnet");
            cmd.arg("build").arg(&p.path).arg("-nologo").args(config_args(&a.config)).args(&a.extra);
            tools::run_passthrough(&mut cmd, json)?.success()
        }
        Kind::Native => native_build(&p, a.config.as_deref(), None, &a.extra, json)?,
    };
    if json {
        out::print_json(&json!({"ok": ok, "project": p.path, "kind": kind_name(p.kind)}));
    }
    Ok(if ok { 0 } else { 1 })
}

fn kind_name(k: Kind) -> &'static str {
    match k {
        Kind::Dotnet => "dotnet",
        Kind::Native => "native",
    }
}

pub fn deploy(a: DeployArgs, json: bool) -> Result<i32> {
    let p = Project::find(a.project.as_deref())?;
    let srv = server::resolve(a.server.as_deref(), Some(&p))?;
    if !srv.dir.join("kiln").is_dir() {
        return Err(CliError::new("not_found", format!("{} has no kiln/ folder: install Kiln there first", srv.dir.display())).into());
    }
    let (ok, id) = match p.kind {
        Kind::Dotnet => {
            let mut cmd = Command::new("dotnet");
            cmd.arg("build")
                .arg(&p.path)
                .arg("-nologo")
                .arg("-t:KilnDeploy")
                .arg(format!("-p:KilnServer={}", srv.dir.display()))
                .args(config_args(&a.config))
                .args(&a.extra);
            if a.reload {
                cmd.arg("-p:KilnReload=true");
            }
            let ok = tools::run_passthrough(&mut cmd, json)?.success();
            let id = if ok { p.property("KilnModId").ok() } else { None };
            (ok, id)
        }
        Kind::Native => {
            if a.reload {
                out::say(json, "note: native mods are never unloaded; the new DLL loads at the next start (kiln run --restart)");
            }
            let ok = native_build(&p, a.config.as_deref(), Some(&srv.dir), &a.extra, json)?;
            (ok, p.native_manifest().ok().and_then(|m| m["id"].as_str().map(str::to_string)))
        }
    };
    if json {
        out::print_json(&json!({"ok": ok, "project": p.path, "server": srv.dir, "via": srv.via, "id": id,
                                "reload": a.reload && p.kind == Kind::Dotnet}));
    }
    Ok(if ok { 0 } else { 1 })
}

pub fn pack(a: PackArgs, json: bool) -> Result<i32> {
    let p = Project::find(a.project.as_deref())?;
    if p.kind == Kind::Native {
        return pack_native(&p, a.config.as_deref(), a.output.as_deref(), json);
    }
    let mut cmd = Command::new("dotnet");
    cmd.arg("build").arg(&p.path).arg("-nologo").arg("-t:KilnPack").args(config_args(&a.config));
    if let Some(o) = &a.output {
        let o = std::path::absolute(o).unwrap_or(o.clone());
        cmd.arg(format!("-p:KilnPackageOutputPath={}/", o.display()));
    }
    let ok = tools::run_passthrough(&mut cmd, json)?.success();
    let mut zip = Value::Null;
    if ok {
        let props = p.properties(&["KilnModId", "Version", "KilnPackageOutputPath"], a.config.as_deref())?;
        let s = |k: &str| props.get(k).and_then(Value::as_str).unwrap_or("").to_string();
        let dir = a.output.clone().map(|o| std::path::absolute(&o).unwrap_or(o)).unwrap_or_else(|| PathBuf::from(s("KilnPackageOutputPath")));
        let dir = if dir.is_relative() { p.dir.join(dir) } else { dir };
        let f = dir.join(format!("{}-{}.zip", s("KilnModId"), s("Version")));
        if f.is_file() {
            zip = Value::String(f.display().to_string());
        }
    }
    if json {
        out::print_json(&json!({"ok": ok, "zip": zip}));
    } else if let Some(z) = zip.as_str() {
        println!("{z}");
    }
    Ok(if ok { 0 } else { 1 })
}

// ---- native (CMake) ----

fn native_build(p: &Project, config: Option<&str>, server: Option<&Path>, extra: &[String], json: bool) -> Result<bool> {
    tools::require("cmake", "CMake 3.24+ builds native mods")?;
    let sdk = tools::sdk_native_dir(None)?;
    let build = p.dir.join("build");
    let cfg = config.unwrap_or("Release");
    let mut conf = Command::new("cmake");
    conf.arg("-S").arg(&p.dir).arg("-B").arg(&build).arg(format!("-DKILN_SDK_DIR={}", sdk.display()));
    conf.arg(format!("-DCMAKE_BUILD_TYPE={cfg}"));
    if !cfg!(windows) {
        tools::require("clang-cl", "the Linux cross build uses clang-cl + lld-link and the xwin CRT/SDK (cargo xwin)")?;
        conf.arg(format!("-DCMAKE_TOOLCHAIN_FILE={}", sdk.join("cmake/clang-cl-xwin.cmake").display()));
        if kiln_manage::util::which("ninja").is_some() && !build.join("CMakeCache.txt").exists() {
            conf.arg("-G").arg("Ninja");
        }
    }
    if let Some(s) = server {
        conf.arg(format!("-DKILN_SERVER={}", s.display()));
    }
    if !tools::run_passthrough(&mut conf, json)?.success() {
        return Ok(false);
    }
    let mut b = Command::new("cmake");
    b.arg("--build").arg(&build).arg("--config").arg(cfg);
    if server.is_some() {
        b.arg("--target").arg("deploy");
    }
    if !extra.is_empty() {
        b.arg("--").args(extra);
    }
    Ok(tools::run_passthrough(&mut b, json)?.success())
}

fn pack_native(p: &Project, config: Option<&str>, output: Option<&Path>, json: bool) -> Result<i32> {
    if !native_build(p, config, None, &[], json)? {
        return Ok(1);
    }
    let m = p.native_manifest()?;
    let id = m["id"].as_str().unwrap_or("native-mod").to_string();
    let version = m["version"].as_str().unwrap_or("0.0.0").to_string();
    let stage = p.dir.join("build/mod");
    if !stage.join(&id).is_dir() {
        return Err(CliError::new("not_found", format!("{} was not staged by kiln_plugin(... NATIVE_JSON ...)", stage.join(&id).display())).into());
    }
    let dir = output.map(Path::to_path_buf).unwrap_or_else(|| p.dir.join("build"));
    std::fs::create_dir_all(&dir)?;
    let zip = std::path::absolute(dir.join(format!("{id}-{version}.zip")))?;
    let _ = std::fs::remove_file(&zip);
    // <id>/ inside the zip, like KilnPack; the pdb stays out
    let ok = if cfg!(windows) {
        Command::new("tar").arg("-a").arg("-cf").arg(&zip).arg("--exclude=*.pdb").arg(&id).current_dir(&stage).status()
    } else {
        tools::require("zip", "packing native mods")?;
        Command::new("zip").arg("-q").arg("-r").arg(&zip).arg(&id).arg("-x").arg("*.pdb").current_dir(&stage).status()
    }
    .map(|s| s.success())
    .unwrap_or(false);
    if json {
        out::print_json(&json!({"ok": ok, "zip": if ok { Value::String(zip.display().to_string()) } else { Value::Null }}));
    } else if ok {
        println!("{}", zip.display());
    }
    Ok(if ok { 0 } else { 1 })
}

// ---- cook ----

pub fn cook(a: CookArgs, json: bool) -> Result<i32> {
    let p = cook_target(a.project.as_deref())?;
    let r = cook_project(&p, a.check, a.game.as_deref(), json, false)?;
    if json {
        out::print_json(&r);
    }
    Ok(if r["ok"] == true { 0 } else { 1 })
}

/// What `kiln cook` cooks: a mod project, or an installed mod's folder (the one with its kiln.mod.json, as under a server's
/// kiln/mods: a server operator cooks the mods there against the server's files, `--game <server folder>`).
fn cook_target(arg: Option<&Path>) -> Result<Project> {
    Project::find(arg).or_else(|e| {
        let dir = match arg {
            Some(p) => p.to_path_buf(),
            None => std::env::current_dir()?,
        };
        let dir = std::path::absolute(&dir).unwrap_or(dir);
        if dir.is_dir() && dir.join("kiln.mod.json").is_file() {
            return Ok(Project { kind: Kind::Native, path: dir.clone(), dir });
        }
        Err(e)
    })
}

/// Runs (or checks) every recipe of the project. `auto`: called by `kiln build`: silent when nothing is to do, and
/// a recipe whose tool or game is missing only warns.
fn cook_project(p: &Project, check: bool, game: Option<&Path>, json: bool, auto: bool) -> Result<Value> {
    let sound_current = write_sound_blocks(p, json, check, auto)?;
    let recipes = p.recipes();
    let mut results = Vec::new();
    let looks = wants_looks(p);
    if recipes.is_empty() && !looks {
        if !auto {
            out::say(json, "no cook recipes (*/cook.json) in this project");
        }
        return Ok(json!({"ok": sound_current, "recipes": results}));
    }
    // a checkout's tools when there is one, else the copy this kiln carries; and a Python that can run them
    let cook = match tools::cook_tools() {
        Ok(t) => t,
        Err(e) if auto => {
            out::say(json, format!("kiln: recipes found but the cook tools cannot run ({e}); skipping the cook"));
            return Ok(json!({"ok": true, "recipes": results}));
        }
        Err(e) => return Err(e),
    };
    let game = game.map(Path::to_path_buf).or_else(tools::client_dir);
    let mut all_ok = sound_current;
    for r in recipes {
        let doc = std::fs::read_to_string(&r).ok().and_then(|t| crate::project::jsonc(&t).ok()).unwrap_or(Value::Null);
        let tool = doc["tool"].as_str().unwrap_or("modelcook").to_string();
        let Some((_, verb)) = cook.script(&tool) else {
            let msg = format!("{}: no cook tool '{tool}' in {}", r.display(), cook.root.display());
            if auto {
                out::say(json, format!("kiln: warning: {msg}"));
                results.push(json!({"recipe": r, "tool": tool, "ok": false, "skipped": msg}));
                continue;
            }
            return Err(CliError::new("tool_missing", msg).into());
        };
        let mut chk = cook.command(&tool).expect("the tool's script was found above");
        chk.arg(verb).arg(&r).arg("--check");
        chk.stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null());
        let needed = !chk.status().map(|s| s.success()).unwrap_or(false);
        if check {
            out::say(json, format!("{}: {}", r.display(), if needed { "cook needed" } else { "up to date" }));
            all_ok &= !needed;
            results.push(json!({"recipe": r, "tool": tool, "needed": needed}));
            continue;
        }
        if !needed {
            if !auto {
                out::say(json, format!("{}: up to date", r.display()));
            }
            results.push(json!({"recipe": r, "tool": tool, "ok": true, "cooked": false}));
            continue;
        }
        let mut cmd = cook.command(&tool).expect("the tool's script was found above");
        match &game {
            Some(g) => {
                cmd.arg("--game").arg(g);
            }
            None if tool == "soundcook" || tool == "animcook" => {
                cmd.arg("--no-game");
            }
            None => {
                let msg = format!("{}: the cook reads your installed game; none found (--game or KILN_GAME)", r.display());
                if auto {
                    out::say(json, format!("kiln: warning: {msg}"));
                    results.push(json!({"recipe": r, "tool": tool, "ok": false, "skipped": msg}));
                    continue;
                }
                return Err(CliError::new("not_found", msg).into());
            }
        }
        cmd.arg(verb).arg(&r).arg("--update-manifest");
        out::say(json, format!("kiln: cooking {} ({tool})", r.display()));
        let ok = tools::run_passthrough(&mut cmd, json)?.success();
        all_ok &= ok;
        results.push(json!({"recipe": r, "tool": tool, "ok": ok, "cooked": true}));
    }
    if looks {
        let (ok, entry) = cook_looks(p, check, game.as_deref(), json, auto, &cook)?;
        all_ok &= ok;
        results.extend(entry);
    }
    Ok(json!({"ok": all_ok, "recipes": results}))
}

/// Include recipes with old generated steps so deleting the last block also removes its steps.
fn has_sound_blocks(p: &Project) -> bool {
    let has_sounds = std::fs::read_dir(p.dir.join("blocks")).into_iter().flatten().flatten().any(|e| {
        e.path().extension().is_some_and(|x| x == "json")
            && std::fs::read_to_string(e.path()).ok().and_then(|t| crate::project::jsonc(&t).ok()).is_some_and(|d| d.get("sounds").is_some())
    });
    has_sounds || std::fs::read_to_string(p.dir.join("assets/cook.json")).ok()
        .and_then(|t| crate::project::jsonc(&t).ok()).is_some_and(|d| d.get("blockSounds").is_some())
}

/// Update or compare the generated sound steps before checking the recipe's cook report.
fn write_sound_blocks(p: &Project, json: bool, check: bool, auto: bool) -> Result<bool> {
    if !has_sound_blocks(p) {
        return Ok(true);
    }
    let cook = match tools::cook_tools() {
        Ok(t) => t,
        Err(e) if auto => {
            out::say(json, format!("kiln: warning: sound blocks need the cook tools ({e})"));
            return Ok(false);
        }
        Err(e) => return Err(e),
    };
    let mut cmd = cook.script_command("tools/soundcook/soundcook.py");
    cmd.arg("blocks").arg(&p.dir);
    if check {
        cmd.arg("--check");
    }
    if !p.dir.join("kiln.mod.json").is_file() && matches!(p.kind, Kind::Dotnet) {
        let id = p.property("KilnModId")?;
        if !id.is_empty() {
            cmd.arg("--mod").arg(id);
        }
    }
    let error = match cmd.output() {
        Ok(o) if o.status.success() || (check && o.status.code() == Some(1)) => {
            let said = String::from_utf8_lossy(&o.stdout);
            if check || said.contains("written") || said.contains("removed") {
                out::say(json, format!("kiln: {}", said.trim()));
            }
            return Ok(o.status.success());
        }
        Ok(o) => String::from_utf8_lossy(&o.stderr).trim().to_string(),
        Err(e) => e.to_string(),
    };
    if auto {
        out::say(json, format!("kiln: warning: sound blocks: {error}"));
        Ok(false)
    } else {
        Err(CliError::new("cook_failed", format!("sound blocks: {error}")).into())
    }
}

/// Whether the project's blocks/*.json may hold looks with files the player's game needs cooked: a blocks folder and no assets/cook.json of modelcook's, which cooks them itself.
fn wants_looks(p: &Project) -> bool {
    if !p.dir.join("blocks").is_dir() {
        return false;
    }
    let recipe = p.dir.join("assets/cook.json");
    let tool = std::fs::read_to_string(&recipe).ok().and_then(|t| crate::project::jsonc(&t).ok()).map(|d| d["tool"].as_str().unwrap_or("modelcook").to_string());
    tool.as_deref() != Some("modelcook")
}

/// The looks of blocks/*.json, cooked with no recipe of the mod's own (`modelcook.py looks`). Nothing when there are none.
fn cook_looks(p: &Project, check: bool, game: Option<&Path>, json: bool, auto: bool, cook: &kiln_manage::cooktools::CookTools) -> Result<(bool, Option<Value>)> {
    let looks = "tools/modelcook/modelcook.py";
    let where_ = p.dir.join("blocks");
    let probe = cook.script_command(looks).arg("looks").arg(&p.dir).arg("--check").output();
    let Ok(probe) = probe else { return Ok((true, None)) };
    let said = String::from_utf8_lossy(&probe.stdout);
    if said.contains("no looks") {
        if !auto && p.recipes().is_empty() {
            out::say(json, "no cook recipes (*/cook.json) in this project, and no looks with files in blocks/*.json");
        }
        return Ok((true, None));
    }
    let needed = !probe.status.success();
    if check {
        out::say(json, format!("{} (looks): {}", where_.display(), if needed { "cook needed" } else { "up to date" }));
        return Ok((!needed, Some(json!({"recipe": where_, "tool": "modelcook", "needed": needed}))));
    }
    if !needed {
        if !auto {
            out::say(json, format!("{} (looks): up to date", where_.display()));
        }
        return Ok((true, Some(json!({"recipe": where_, "tool": "modelcook", "ok": true, "cooked": false}))));
    }
    let Some(game) = game else {
        let msg = format!("{} (looks): the cook reads your installed game; none found (--game or KILN_GAME)", where_.display());
        if auto {
            out::say(json, format!("kiln: warning: {msg}"));
            return Ok((false, Some(json!({"recipe": where_, "tool": "modelcook", "ok": false, "skipped": msg}))));
        }
        return Err(CliError::new("not_found", msg).into());
    };
    let mut cmd = cook.script_command(looks);
    cmd.arg("--game").arg(game).arg("looks").arg(&p.dir).arg("--update-manifest");
    // a model's GUID follows from the mod's id: a project whose kiln.mod.json is made by the build says it in KilnModId
    if !p.dir.join("kiln.mod.json").is_file() && matches!(p.kind, Kind::Dotnet) {
        let id = p.property("KilnModId")?;
        if !id.is_empty() {
            cmd.arg("--mod").arg(id);
        }
    }
    out::say(json, format!("kiln: cooking the looks of {}", where_.display()));
    let ok = tools::run_passthrough(&mut cmd, json)?.success();
    Ok((ok, Some(json!({"recipe": where_, "tool": "modelcook", "ok": ok, "cooked": true}))))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sound_blocks_are_checked_updated_and_removed_without_a_game() {
        let dir = std::env::temp_dir().join(format!("kiln-sound-check-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("blocks")).unwrap();
        std::fs::create_dir_all(dir.join("audio")).unwrap();
        std::fs::write(dir.join("kiln.mod.json"), r#"{"id":"test-sounds"}"#).unwrap();
        std::fs::write(dir.join("audio/a.wav"), "RIFF").unwrap();
        std::fs::write(dir.join("blocks/sounds.json"), r#"{"sounds":{"ping":{"clips":["audio/a.wav"]}}}"#).unwrap();
        let p = Project { kind: Kind::Native, path: dir.clone(), dir: dir.clone() };
        assert!(!write_sound_blocks(&p, false, true, false).unwrap());
        assert!(!dir.join("assets/cook.json").exists());
        assert!(write_sound_blocks(&p, false, false, false).unwrap());
        assert!(write_sound_blocks(&p, false, true, false).unwrap());
        std::fs::remove_file(dir.join("blocks/sounds.json")).unwrap();
        assert!(!write_sound_blocks(&p, false, true, false).unwrap());
        assert!(write_sound_blocks(&p, false, false, false).unwrap());
        let doc: Value = serde_json::from_str(&std::fs::read_to_string(dir.join("assets/cook.json")).unwrap()).unwrap();
        assert_eq!(doc["steps"], json!([]));
        assert!(doc.get("blockSounds").is_none());
        // An invalid block must fail an explicit cook instead of silently cooking the old recipe.
        std::fs::write(dir.join("blocks/sounds.json"), r#"{"sounds":{"ping":{"clips":[]}}}"#).unwrap();
        assert!(write_sound_blocks(&p, false, false, false).is_err());
        std::fs::remove_dir_all(dir).unwrap();
    }
}
