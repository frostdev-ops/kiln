// SPDX-License-Identifier: MIT
//! `kiln`: the modder's command line for Kiln (docs/kiln/modding/cli.md). Every command that returns data takes
//! `--json` (one JSON document on stdout; errors as `{"ok":false,"error":{"code","message"}}`); exit codes: 0 ok,
//! 1 failure, 2 usage.
//!
//! It drives what already exists instead of re-implementing it: `dotnet new` (Kiln.Templates), `dotnet build` with
//! Kiln.Sdk's targets (KilnDeploy, KilnPack), CMake + `kiln_plugin` for native mods, the cook tools in the Kiln
//! repository (tools/modelcook, tools/impact, tools/soundcook), the control folder (`reload`, `test`), kiln.log
//! (kiln_manage::logs), Kiln.Testing's result files, the launcher's server instances and the dev labs, the linter
//! (kiln_manage::lint), the game-data catalogs (kiln_manage::catalog) and netcoredbg (`debug`).

mod build;
mod control;
mod debug;
mod blocks;
mod logs;
mod new;
mod out;
mod project;
mod server;
mod tools;

#[cfg(feature = "catalog")]
mod catalog;
#[cfg(feature = "lint")]
mod lint;

use clap::{Parser, Subcommand};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "kiln", version, about = "Kiln mod development: new, build, cook, run, deploy, reload, test, debug, lint, pack, logs, catalog",
          after_help = "Server resolution: --server (a folder, a lab name or a server instance id), else $KILN_SERVER, else the project's KilnServer property.\nDocs: docs/kiln/modding/cli.md")]
struct Cli {
    /// Machine-readable output: one JSON document on stdout.
    #[arg(long, global = true)]
    json: bool,
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Create a mod from a template (dotnet new kiln-<kind>); --list shows them.
    New(new::NewArgs),
    /// Build a mod project (dotnet build, or CMake for native mods); runs `kiln cook` first when a recipe is out of date.
    Build(build::BuildArgs),
    /// Make the game-derived files of a mod from the installed game (cook.json recipes: models, Impact, sounds).
    Cook(build::CookArgs),
    /// Copy the built mod into a server's (or game's) kiln/mods (native: kiln/native); --reload hot-reloads it.
    Deploy(build::DeployArgs),
    /// Package the mod: <id>-<version>.zip (never with cooked game files).
    Pack(build::PackArgs),
    /// Start (or stop, restart, check) a local server: a dev lab or a launcher/kilnctl server instance.
    Run(server::RunArgs),
    /// Hot-reload a mod in the running server (control folder `reload <id>`).
    Reload(control::ReloadArgs),
    /// Run a mod's [KilnTest] methods in the running server and print the report.
    Test(control::TestArgs),
    /// Attach a debugger to a running local server's mods (--dap: a debug adapter for editors; never ends the server).
    Debug(debug::DebugArgs),
    /// Show or follow kiln/logs/kiln.log (filter by mod or level).
    Logs(logs::LogsArgs),
    /// Lint mod files (manifests, data packs, recipes) with the Kiln linter.
    Lint(LintArgs),
    /// What a server's mods' building blocks became: items, templates, programs, and what went wrong.
    Blocks(blocks::BlocksArgs),
    /// The local game-data catalog (items, templates, perks, ...) made from your installed game.
    Catalog(CatalogArgs),
}

#[derive(clap::Args)]
pub struct LintArgs {
    /// Files, mod folders or a profile folder (default: the current folder).
    pub paths: Vec<PathBuf>,
    /// The game-data catalog to check references against (default: kiln catalog's).
    #[arg(long)]
    pub catalog: Option<PathBuf>,
    /// Lint across the mods of this profile folder.
    #[arg(long)]
    pub profile: Option<PathBuf>,
    /// error | warning | info | hint
    #[arg(long, value_name = "LEVEL")]
    pub min_severity: Option<String>,
    /// Turn a rule off (repeatable), e.g. --off KL203.
    #[arg(long = "off", value_name = "RULE")]
    pub off: Vec<String>,
    /// List the linter's rules (code, name, severity, title).
    #[arg(long)]
    pub list_rules: bool,
    /// Read the file text from stdin (an unsaved editor buffer); --stdin-path names it.
    #[arg(long)]
    pub stdin: bool,
    #[arg(long, value_name = "PATH")]
    pub stdin_path: Option<PathBuf>,
}

#[derive(clap::Args)]
pub struct CatalogArgs {
    #[command(subcommand)]
    pub cmd: CatalogCmd,
}

#[derive(Subcommand)]
pub enum CatalogCmd {
    /// Where the catalogs are and whether they match the installed game.
    Status {
        /// The game (client or server) folder to compare with (default: detected).
        #[arg(long)]
        game: Option<PathBuf>,
    },
    /// Make or refresh the catalogs from the installed game (only when outdated, unless --force).
    Build {
        /// client | server (default: every side found)
        #[arg(long)]
        side: Option<String>,
        /// The game folder (default: detected through Steam).
        #[arg(long)]
        game: Option<PathBuf>,
        #[arg(long)]
        force: bool,
        /// Also write the reflection dump for locally generated Kiln.Game types (<catalog>/types/, docs/kiln/modding/catalogs.md).
        #[arg(long)]
        types: bool,
    },
    /// Print the catalog folder and files.
    Path,
    /// Find entries by name, display name, id or GUID.
    Search {
        query: String,
        /// items | templates | perks | buffs | skills | sequences | ...
        #[arg(long)]
        section: Option<String>,
        #[arg(long, default_value_t = 20)]
        limit: usize,
        /// client | server
        #[arg(long)]
        side: Option<String>,
    },
}

fn main() {
    let cli = Cli::parse();
    let json = cli.json;
    let r = match cli.cmd {
        Cmd::New(a) => new::run(a, json),
        Cmd::Build(a) => build::build(a, json),
        Cmd::Cook(a) => build::cook(a, json),
        Cmd::Deploy(a) => build::deploy(a, json),
        Cmd::Pack(a) => build::pack(a, json),
        Cmd::Run(a) => server::run(a, json),
        Cmd::Reload(a) => control::reload(a, json),
        Cmd::Test(a) => control::test(a, json),
        Cmd::Debug(a) => debug::run(a, json),
        Cmd::Logs(a) => logs::run(a, json),
        Cmd::Lint(a) => lint_cmd(a, json),
        Cmd::Blocks(a) => blocks::run(a, json),
        Cmd::Catalog(a) => catalog_cmd(a, json),
    };
    std::process::exit(match r {
        Ok(code) => code,
        Err(e) => out::fail(&e, json),
    });
}

#[cfg(feature = "lint")]
fn lint_cmd(a: LintArgs, json: bool) -> anyhow::Result<i32> {
    lint::run(a, json)
}
#[cfg(not(feature = "lint"))]
fn lint_cmd(_: LintArgs, _: bool) -> anyhow::Result<i32> {
    Err(out::CliError::new("unavailable", "this kiln was built without the linter (cargo feature `lint`)").into())
}

#[cfg(feature = "catalog")]
fn catalog_cmd(a: CatalogArgs, json: bool) -> anyhow::Result<i32> {
    catalog::run(a, json)
}
#[cfg(not(feature = "catalog"))]
fn catalog_cmd(_: CatalogArgs, _: bool) -> anyhow::Result<i32> {
    Err(out::CliError::new("unavailable", "this kiln was built without catalogs (cargo feature `catalog`)").into())
}
