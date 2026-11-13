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

/// The id a cook derives GUIDs from, for a project whose kiln.mod.json is made by its build.
fn cook_mod_id(p: &Project) -> Result<Option<String>> {
    if !p.dir.join("kiln.mod.json").is_file() && matches!(p.kind, Kind::Dotnet) {
        return Ok(Some(p.property("KilnModId")?).filter(|id| !id.is_empty()));
    }
    Ok(None)
}

/// Runs (or checks) every recipe of the project (kiln_manage::cook::cook_folder: the launcher cooks with the same
/// code). `auto`: called by `kiln build`: silent when nothing is to do, and a recipe whose tool or game is missing
/// only warns.
fn cook_project(p: &Project, check: bool, game: Option<&Path>, json: bool, auto: bool) -> Result<Value> {
    use kiln_manage::cook;
    if !cook::wants_tools(&p.dir) {
        if !auto {
            out::say(json, "no cook recipes (*/cook.json) in this project");
        }
        return Ok(json!({"ok": true, "recipes": []}));
    }
    // a checkout's tools when there is one, else the copy this kiln carries; and a Python that can run them
    let tools = match tools::cook_tools() {
        Ok(t) => t,
        Err(e) if auto => {
            out::say(json, format!("kiln: recipes found but the cook tools cannot run ({e}); skipping the cook"));
            return Ok(json!({"ok": true, "recipes": []}));
        }
        Err(e) => return Err(e),
    };
    let opts = cook::CookOptions { game: game.map(Path::to_path_buf).or_else(tools::client_dir), check, mod_id: cook_mod_id(p)?, strict: !auto };
    let r = cook::cook_folder(&tools, &p.dir, &opts, &mut |line| out::say(json, line)).map_err(|stop| CliError::new(stop.code, stop.message))?;
    Ok(json!({"ok": r.ok, "recipes": r.recipes}))
}

/// Update or compare the generated sound steps (the first step of a cook).
#[cfg(test)]
fn write_sound_blocks(p: &Project, json: bool, check: bool, auto: bool) -> Result<bool> {
    use kiln_manage::cook;
    let tools = tools::cook_tools()?;
    let opts = cook::CookOptions { game: None, check, mod_id: cook_mod_id(p)?, strict: !auto };
    cook::sound_blocks(&tools, &p.dir, &opts, &mut |line| out::say(json, line)).map_err(|stop| CliError::new(stop.code, stop.message).into())
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
