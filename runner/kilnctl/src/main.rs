// SPDX-License-Identifier: MIT
//! kilnctl: installs, deploys Kiln into and runs Enshrouded dedicated servers under Wine on Linux, without the Steam
//! client. One instance file per server: ~/.config/kiln/instances/<name>.toml. See docs/kiln/kilnctl.md.
mod bundle;
mod client;
mod config;
mod console;
mod gate;
mod kiln;
mod lock;
mod ops;
mod pack;
mod preflight;
mod profiles;
mod procs;
mod regproof;
mod registry;
mod report;
mod run;
mod server;
mod sigport;
mod steam;
mod tick_compare;
mod util;
mod wine;

use clap::{Args, Parser, Subcommand};
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

#[derive(Parser)]
#[command(name = "kilnctl", version = kiln_manage::VERSION, about = "Run Enshrouded dedicated servers with Kiln under Wine (SteamCMD, no Steam client)")]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

/// The lab gate (a copy of the instance with policy any and Kiln's self tests; docs/kiln/sigport.md)
#[derive(Args, Clone)]
struct GateArgs {
    /// Test mods (mods/tests/<id>), comma separated [default: world, players, chat, items, settings, gameplay,
    /// hooks, combat, spawn, content, registry-proof (the registry proof's lab evidence)]
    #[arg(long, value_delimiter = ',')]
    tests: Option<Vec<String>>,
    /// UDP query port of the copy [default: the instance's + 1]
    #[arg(long)]
    gate_port: Option<u16>,
    /// Seconds to wait for the copy's world
    #[arg(long, default_value_t = 300)]
    gate_boot_timeout: u64,
    /// Seconds to wait for the tests' summaries after the world is up
    #[arg(long, default_value_t = 300)]
    gate_test_timeout: u64,
    /// Also run the instance's own mods and native plugins in the copy (off: a mod may reach outside the folder)
    #[arg(long)]
    gate_instance_mods: bool,
}

impl GateArgs {
    fn opts(&self) -> gate::GateOpts {
        gate::GateOpts {
            tests: self.tests.clone().unwrap_or_else(|| gate::DEFAULT_TESTS.iter().map(|s| s.to_string()).collect()),
            port: self.gate_port,
            boot_timeout: Duration::from_secs(self.gate_boot_timeout),
            test_timeout: Duration::from_secs(self.gate_test_timeout),
            exe: None,
            sigdb_tree: None,
            record: None,
            instance_mods: self.gate_instance_mods,
        }
    }
}

#[derive(Subcommand)]
enum Cmd {
    /// Write the instance file ~/.config/kiln/instances/<NAME>.toml
    Init {
        name: String,
        /// Server folder (created by install)
        #[arg(long)]
        dir: PathBuf,
        /// UDP query port
        #[arg(long, default_value_t = config::DEFAULT_PORT)]
        port: u16,
        /// ge (newest GE-Proton, pinned in the file), ge:<GE-Proton folder>, system, or a wine path
        #[arg(long, default_value = "ge")]
        wine: String,
        /// Wine prefix [default: <dir>.pfx]
        #[arg(long)]
        prefix: Option<PathBuf>,
        /// Savegame backups [default: <dir>.backups]
        #[arg(long)]
        backup_dir: Option<PathBuf>,
        /// Kiln policy written to kiln/kiln.json on deploy
        #[arg(long, value_enum, default_value = "verified-only")]
        policy: config::PolicyArg,
        /// Deploy the sample mod and native plugin too
        #[arg(long)]
        samples: bool,
        /// Kiln source tree [default: the tree kilnctl was built from]
        #[arg(long)]
        kiln_source: Option<PathBuf>,
        /// Kiln build output (KILN_OUT) [default: <kiln-source>/native/build]
        #[arg(long)]
        kiln_build: Option<PathBuf>,
        /// Overwrite an existing instance file
        #[arg(long)]
        force: bool,
    },
    /// Install or validate the server with SteamCMD (app 2278520, Windows depot)
    Install {
        name: String,
        /// Reflink-copy (cp --reflink) the game files of an existing install first; SteamCMD then only validates
        #[arg(long, value_name = "DIR")]
        reflink_from: Option<PathBuf>,
    },
    /// Create or update the Wine prefix (wineboot -i, null graphics driver) and verify it
    Prefix { name: String },
    /// Lay Kiln out in the server folder (tools/deploy-server.sh)
    Deploy {
        name: String,
        /// Kiln build output to deploy [default: kiln.build of the instance]
        #[arg(long, value_name = "KILN_OUT")]
        from: Option<PathBuf>,
        /// Also deploy the sample mod and native plugin
        #[arg(long)]
        samples: bool,
        /// Let deploy-server.sh build Kiln first (default: deploy the existing build)
        #[arg(long)]
        build: bool,
        /// Install this runtime bundle instead of running deploy-server.sh: its zip (kiln-runtime-*.zip) or the folder it
        /// was unpacked to (kiln-runtime.json at its root)
        #[arg(long, value_name = "BUNDLE", conflicts_with_all = ["from", "build"])]
        bundle: Option<PathBuf>,
    },
    /// Check that the instance can start (exit 1 on failure)
    Preflight { name: String },
    /// Run the server in the foreground (systemd's ExecStart); SIGINT/SIGTERM stop it with a save
    Run { name: String },
    /// Start the server in the background and wait for the world to run
    Start {
        name: String,
        /// Start the systemd user unit kiln@<NAME> instead of a detached runner
        #[arg(long)]
        systemd: bool,
        /// Seconds to wait for the world
        #[arg(long, default_value_t = 300)]
        timeout: u64,
    },
    /// Stop the server: SIGINT, wait for the save, wineserver -w
    Stop {
        name: String,
        /// Seconds to wait for the save [default: stop_timeout of the instance]
        #[arg(long)]
        timeout: Option<u64>,
    },
    /// Show the instance's state (exit 0 running, 3 stopped)
    Status { name: String },
    /// Print the game log (or Kiln's log, or the detached runner's output)
    Logs {
        name: String,
        #[arg(short = 'n', long, default_value_t = 40)]
        lines: usize,
        #[arg(short, long)]
        follow: bool,
        /// kiln/logs/kiln.log
        #[arg(long, conflicts_with = "run")]
        kiln: bool,
        /// Output of kilnctl run started by kilnctl start
        #[arg(long)]
        run: bool,
    },
    /// Stop, back up the save, update with SteamCMD, check Kiln's symbols on the new build; on a build without a
    /// verified record port the symbols (sigport), boot the lab gate and mark the build verified on green; start again
    Update {
        name: String,
        /// Leave the server stopped afterwards
        #[arg(long)]
        no_start: bool,
        /// Seconds to wait for the world when starting again
        #[arg(long, default_value_t = 300)]
        timeout: u64,
        /// No stop, backup, SteamCMD or start: port + gate as if --new-exe had just been installed; writes no record
        #[arg(long)]
        dry_run: bool,
        /// With --dry-run: the exe to treat as the update [default: the instance's exe]
        #[arg(long, requires = "dry_run")]
        new_exe: Option<PathBuf>,
        /// The exe the definitions resolve on [default: the cached exe of the build before the update]
        #[arg(long)]
        old: Option<PathBuf>,
        /// Only report (the pre-sigport behaviour)
        #[arg(long)]
        no_port: bool,
        /// Port but do not boot the lab gate (then nothing is marked)
        #[arg(long)]
        no_gate: bool,
        /// Never write a verified record (leave it to a human)
        #[arg(long)]
        no_mark: bool,
        #[command(flatten)]
        gate: GateArgs,
    },
    /// Port Kiln's symbols to the instance's exe (tools/sigport/port.py), boot the lab gate; --mark writes the verified
    /// record on green
    Port {
        name: String,
        /// The exe the definitions resolve on [default: the newest cached exe with a verified record]
        #[arg(long)]
        old: Option<PathBuf>,
        /// The exe to port to [default: the instance's exe]
        #[arg(long)]
        new: Option<PathBuf>,
        /// Definitions (<tree>/server.json) [default: the deployed kiln/sigdb/server.json]
        #[arg(long)]
        defs: Option<PathBuf>,
        /// Do not boot the lab gate
        #[arg(long)]
        no_gate: bool,
        /// Write kiln/sigdb/builds/server/<sha256>.json with "verified": true when everything is green
        #[arg(long)]
        mark: bool,
        #[command(flatten)]
        gate: GateArgs,
    },
    /// Boot the lab gate on the instance as it is (policy any + the self tests); --baseline records the result as the
    /// instance's baseline (verified builds only)
    Gate {
        name: String,
        #[arg(long)]
        baseline: bool,
        #[command(flatten)]
        gate: GateArgs,
    },
    /// The registry proof of a game build (reflection diff, formulas, the lab's round trip, values diff, aliases): writes
    /// the regmap record (verified only when every step is green) and, with --mark, puts it into the instance
    Regproof {
        /// Instance name: its Kiln tree, exe, Kiln data folder and gate copy [default: none; then --tree]
        name: Option<String>,
        /// server | client
        #[arg(long, default_value = "server")]
        side: String,
        /// The Kiln tree (regmap/, schema/, tools/) [default: the instance's kiln.source]
        #[arg(long)]
        tree: Option<PathBuf>,
        /// The new build's exe, read as data and never run [default: the instance's]
        #[arg(long)]
        new_exe: Option<PathBuf>,
        /// The build the regmap was last proven on [default: the newest cached exe whose build has a verified regmap
        /// record, in the instance's kiln/regmap or the tree]
        #[arg(long)]
        old_exe: Option<PathBuf>,
        /// The new build's reflection dump (kfc.py types --closure ... --json-out) instead of its exe
        #[arg(long)]
        new_refl: Option<PathBuf>,
        /// The old build's reflection dump instead of its exe
        #[arg(long)]
        old_refl: Option<PathBuf>,
        /// The candidate record (regmap_check.py --record) instead of running it
        #[arg(long)]
        record: Option<PathBuf>,
        /// The new build's id table (regmap_check.py --ids-out)
        #[arg(long)]
        ids: Option<PathBuf>,
        /// The previous build's id table [default: the lab's data folder, else the maintainer folder ($KILN_REGMAP_IDS)]
        #[arg(long)]
        prev_ids: Option<PathBuf>,
        /// Lab outputs: files, or folders such as a Kiln data folder (round-trip suites, values exports, kiln.content's
        /// id tables; repeatable) [default with an instance: its kiln/data and its gate copy's]
        #[arg(long)]
        evidence: Vec<PathBuf>,
        /// The game's data build [default: the .kfc beside the new exe]
        #[arg(long)]
        game_build: Option<String>,
        /// Steam's build id [default: the instance's app manifest]
        #[arg(long)]
        steam_build: Option<String>,
        /// Output folder, outside the repository [default: <state>/regproof/<name>/<sha12>-<time>]
        #[arg(long)]
        out: Option<PathBuf>,
        /// No earlier build to compare with (waives the old reflection, values and id table)
        #[arg(long)]
        first_build: bool,
        /// A row whose reflection change a human reviewed (`items.speed`; repeatable)
        #[arg(long)]
        accept: Vec<String>,
        /// Write the verified record into the instance's kiln/regmap/<side>/ (only when every step is green)
        #[arg(long, requires = "name")]
        mark: bool,
        /// With --mark: replace a verified record of the same build (the old one stays as .json.bak)
        #[arg(long, requires = "mark")]
        replace: bool,
        /// A client record mirrored from this server build's verified record (owner ruling: no client is run for a
        /// proof): a row is ok only where the server proof has it ok and the client exe's layout of it is the server's.
        /// With --side client, --tree, the client's --new-exe and --server-exe (or --server-refl)
        #[arg(long, conflicts_with_all = ["name", "mark"])]
        mirror: Option<PathBuf>,
        /// With --mirror: the proven server build's exe, read as data and never run
        #[arg(long, requires = "mirror")]
        server_exe: Option<PathBuf>,
        /// With --mirror: the server exe's reflection dump instead of the exe
        #[arg(long, requires = "mirror")]
        server_refl: Option<PathBuf>,
    },
    /// Back up the savegame folder to a timestamped tar.gz in the backup dir
    Backup { name: String },
    /// Run a command line on the running server as the console (Kiln's console folder) and print its output
    Cmd {
        /// Instance name, or a path to a server folder (e.g. ~/srv/enshrouded/labs/api)
        name: String,
        /// The command line ("time set 7", "/help"); several words are joined with spaces
        #[arg(required = true, num_args = 1.., allow_hyphen_values = true)]
        line: Vec<String>,
        /// Run as another source: console | remote:<name>[:user] | steam:<SteamID64>[:name] | player:<name>
        #[arg(long = "as", value_name = "SOURCE")]
        source: Option<String>,
        /// Print completions for the (partial) line instead of running it
        #[arg(long)]
        complete: bool,
        /// Print the raw result JSON
        #[arg(long)]
        json: bool,
        /// Seconds to wait for the answer
        #[arg(long, default_value_t = 15)]
        timeout: u64,
    },
    /// List instances
    List,
    /// Write a support report: one zip with Kiln's logs, state files, the profile, listings of the game folder and a
    /// summary with findings; home folder, user and machine names, Steam ids and IP addresses are replaced
    Report {
        /// Instance name [default: --dir, else the client Steam knows]
        name: Option<String>,
        /// A server or client folder
        #[arg(long, conflicts_with = "name")]
        dir: Option<PathBuf>,
        /// The zip to write [default: ./kiln-report-<date>.zip]
        #[arg(long)]
        out: Option<PathBuf>,
        /// Print summary.txt instead of writing the zip
        #[arg(long, conflicts_with = "out")]
        print: bool,
        /// Leave out another mod's own file (its path in the zip, e.g. game/BuildingTools.log; repeatable), or
        /// `other-mods` for all of them. Kiln's own logs, its settings and the listings always stay in
        #[arg(long, value_name = "PATH")]
        exclude: Vec<String>,
        /// Print what the report holds (each file, its size and what was replaced in it) instead of writing the zip
        #[arg(long, conflicts_with_all = ["out", "print"])]
        contents: bool,
    },
    /// Runtime bundles: create, verify, install into a game folder, uninstall (docs/kiln/launcher/bundle.md)
    Bundle {
        #[command(subcommand)]
        cmd: BundleCmd,
    },
    /// Kiln in the game client: status, install, uninstall, profile activation, play-once (never Steam's config)
    Client {
        #[command(subcommand)]
        cmd: ClientCmd,
    },
    /// The registry stage's plans: status, and a rollback of the next start to the previous (last good) plan
    Registry {
        #[command(subcommand)]
        cmd: RegistryCmd,
    },
    /// The launcher's mod library ($KILN_DATA_DIR or ~/.local/share/kiln): import, list
    Library {
        #[command(subcommand)]
        cmd: LibraryCmd,
    },
    /// The launcher's profiles: create, add mods, show with the resolved load order
    Profile {
        #[command(subcommand)]
        cmd: ProfileCmd,
    },
    /// Crucible packs (.kilnpack): build from a profile, verify, install as an instance, update, diff, undo, uninstall,
    /// put the server part on a server folder
    Pack {
        #[command(subcommand)]
        cmd: pack::PackCmd,
    },
    /// Compare two tick-stats.jsonl files (before/after moving a mod's work into tasks): per-world step time and
    /// steps over budget, per-mod Pre/Post time and share of the step, and the tasks section when present (T12)
    TickCompare {
        /// The "before" tick-stats.jsonl (kiln/logs/tick-stats.jsonl of the first run)
        before: PathBuf,
        /// The "after" tick-stats.jsonl, same save and route as the first
        after: PathBuf,
        /// Print the report as one JSON document instead of the text tables
        #[arg(long)]
        json: bool,
    },
}

#[derive(Subcommand)]
enum LibraryCmd {
    /// Import folders, zips or DLLs (Kiln mods, native mods, EMF mods, plugins, bare DLLs)
    Import {
        #[arg(required = true)]
        paths: Vec<PathBuf>,
    },
    /// List the library (optionally one kind: kiln, native, plugin, emf); "(changed on disk)" marks an entry whose
    /// files were changed in its folder since its import
    List { kind: Option<String> },
    /// Use the changed files of an entry edited in its Library folder: kind:id[@version] (the newest by default)
    TakeOver { entry: String },
    /// Replace one native mod DLL in a profile; game follows at the next activation.
    ReplaceDll {
        entry: String,
        file: PathBuf,
        #[arg(long)]
        profile: String,
        /// Accept filename and detected-kind warnings.
        #[arg(long)]
        confirm: bool,
    },
    /// Roll a mod in a profile back to an earlier version an update kept: kind:id[@version] (the newest kept by
    /// default); the version rolled away from is kept. The game folder follows at the profile's next activation
    Rollback {
        profile: String,
        entry: String,
    },
}

#[derive(Subcommand)]
enum RegistryCmd {
    /// The plan the last start ran on, the previous (last good) plan, a rollback in effect or asked for (read-only)
    Status {
        /// Server instance [default: --dir, else the client Steam knows]
        name: Option<String>,
        /// A server or client folder
        #[arg(long, conflicts_with = "name")]
        dir: Option<PathBuf>,
    },
    /// Start the next boot on the previous (last good) registry plan: its mods' edits as they were then, every start
    /// after it too until --clear. Says what changes back; never touches saves. The start refuses the plan whole (and
    /// runs the mods' current edits) when one of its mods is not loaded at the plan's version
    Rollback {
        /// Server instance [default: --dir, else the client Steam knows]
        name: Option<String>,
        /// A server or client folder
        #[arg(long, conflicts_with = "name")]
        dir: Option<PathBuf>,
        /// Roll back to this plan (a hash in kiln/data/content/registry/plans) instead of the previous one
        #[arg(long)]
        to: Option<String>,
        /// Remove the rollback: the next start runs on the mods' current registry edits again
        #[arg(long, conflicts_with_all = ["to", "dry_run"])]
        clear: bool,
        /// Say what would change back; write nothing
        #[arg(long)]
        dry_run: bool,
        /// Allow a folder inside a Steam library (the real game)
        #[arg(long)]
        real: bool,
    },
    /// Effect replay of ShroudForge and EML packages (opt-in per package): Kiln records each package's effect on the
    /// registry by default; an opted-in package is replayed from that record after a game update when its Lua no longer
    /// runs. Without --opt-in/--opt-out: what the opt-in file holds and what the last start did with each package
    Replay {
        /// Server instance [default: --dir, else the client Steam knows]
        name: Option<String>,
        /// A server or client folder
        #[arg(long, conflicts_with = "name")]
        dir: Option<PathBuf>,
        /// Opt this package in: shroudforge:<id>, eml:<id>, or an <id> the last start names once
        #[arg(long, value_name = "MOD", conflicts_with = "opt_out")]
        opt_in: Option<String>,
        /// Opt this package out (its effect is still recorded, never replayed)
        #[arg(long, value_name = "MOD")]
        opt_out: Option<String>,
        /// Allow a folder inside a Steam library (the real game)
        #[arg(long)]
        real: bool,
    },
}

#[derive(Subcommand)]
enum ProfileCmd {
    /// A new profile (the id comes from the name)
    Create {
        name: String,
        /// Copy the mods and configs of this profile
        #[arg(long)]
        copy_from: Option<String>,
    },
    /// Add library mods: kind:id[@version] (the newest version by default)
    Add {
        profile: String,
        #[arg(required = true)]
        mods: Vec<String>,
        /// Add them switched off
        #[arg(long)]
        disabled: bool,
    },
    /// The profile's mods and what would load
    Show {
        profile: String,
        #[arg(long, default_value = "client")]
        side: String,
    },
}

#[derive(Subcommand)]
enum BundleCmd {
    /// Zip a staging folder (tools/package/stage.sh) into kiln-runtime-<version>+<date>.<git>.zip, deterministically
    Create {
        /// Staging folder: kiln-runtime.json + one folder per component
        stage: PathBuf,
        /// Output folder
        #[arg(long, default_value = "dist")]
        out: PathBuf,
        /// Git id in the version [default: the source tree's HEAD]
        #[arg(long)]
        git: Option<String>,
        /// Commit time (unix seconds) for entry times and "created" [default: HEAD's commit time]
        #[arg(long)]
        epoch: Option<i64>,
    },
    /// Check every file's size and sha256 against kiln-runtime.json, and that the zip or folder holds nothing else
    Verify {
        /// The bundle: its zip, or the folder it was unpacked to (kiln-runtime.json at its root)
        bundle: PathBuf,
    },
    /// Install or upgrade a bundle into a game folder (client or server, by its exe); writes kiln/.install.json
    Install {
        /// The bundle: its zip, or the folder it was unpacked to (kiln-runtime.json at its root)
        bundle: PathBuf,
        /// The game folder (the one with enshrouded.exe or enshrouded_server.exe)
        #[arg(long)]
        dir: PathBuf,
        /// Components, comma separated: an explicit choice (defaults left out stay off on later upgrades) [default: the
        /// bundle's defaults for the side; an upgrade keeps the installed set and adds new or newly default components
        /// that were not turned off]
        #[arg(long, value_delimiter = ',')]
        components: Option<Vec<String>>,
        /// Move ShroudForge's proxies to the backup instead of refusing (never chained)
        #[arg(long)]
        switch_from_shroudforge: bool,
        /// Allow a folder inside a Steam library (the real game)
        #[arg(long)]
        real: bool,
    },
    /// Remove an installed bundle, restore what it replaced byte-identically, keep kiln/
    Uninstall {
        #[arg(long)]
        dir: PathBuf,
        /// Allow a folder inside a Steam library (the real game)
        #[arg(long)]
        real: bool,
    },
}

#[derive(Subcommand)]
enum ClientCmd {
    /// What is installed, the proxy, the active profile, running?, Steam's launch options (read-only), the last boot
    Status {
        /// Client folder [default: the client Steam knows]
        #[arg(long)]
        dir: Option<PathBuf>,
    },
    /// Install or upgrade a runtime bundle into the client folder
    Install {
        /// The bundle: its zip, or the folder it was unpacked to (kiln-runtime.json at its root)
        bundle: PathBuf,
        #[arg(long)]
        dir: Option<PathBuf>,
        /// Components, comma separated: an explicit choice, as for `bundle install` [default: the defaults; an
        /// upgrade keeps the installed set and adds new or newly default components that were not turned off]
        #[arg(long, value_delimiter = ',')]
        components: Option<Vec<String>>,
        #[arg(long)]
        switch_from_shroudforge: bool,
        /// Allow the real Steam client folder
        #[arg(long)]
        real: bool,
    },
    /// Uninstall: restores the original dinput8.dll byte-identically, keeps kiln/
    Uninstall {
        #[arg(long)]
        dir: Option<PathBuf>,
        #[arg(long)]
        real: bool,
    },
    /// Activate a launcher profile for the client (kiln/profile.json, chain.ini [profile], <configDir>/native.json)
    Activate {
        /// Profile id (<data>/profiles/<id>)
        profile: Option<String>,
        /// Back to the legacy layout (kiln/mods)
        #[arg(long, conflicts_with = "profile")]
        deactivate: bool,
        #[arg(long)]
        dir: Option<PathBuf>,
        #[arg(long)]
        real: bool,
    },
    /// Write kiln/play-once.ini for the next start only: vanilla | no-mods | profile (--profile) | clear
    PlayOnce {
        mode: String,
        /// mode profile: the profile to boot once
        #[arg(long)]
        profile: Option<String>,
        #[arg(long)]
        dir: Option<PathBuf>,
        #[arg(long)]
        real: bool,
    },
}

fn dispatch(cmd: Cmd) -> anyhow::Result<i32> {
    use config::load;
    match cmd {
        Cmd::Init { name, dir, port, wine, prefix, backup_dir, policy, samples, kiln_source, kiln_build, force } => {
            let policy = policy.into();
            ops::init(ops::InitArgs { name, dir, port, wine, prefix, backup_dir, policy, samples, kiln_source, kiln_build, force })?
        }
        Cmd::Install { name, reflink_from } => ops::install(&load(&name)?, reflink_from.as_deref())?,
        Cmd::Prefix { name } => ops::prefix(&load(&name)?)?,
        Cmd::Deploy { name, from, samples, build, bundle } => match bundle {
            Some(bundle) => ops::deploy_bundle(&load(&name)?, &bundle, samples)?,
            None => ops::deploy(&load(&name)?, from.as_deref(), samples, build)?,
        },
        Cmd::Preflight { name } => return ops::preflight_cmd(&load(&name)?),
        Cmd::Run { name } => return run::run(&load(&name)?),
        Cmd::Start { name, systemd, timeout } => ops::start(&load(&name)?, systemd, Duration::from_secs(timeout))?,
        Cmd::Stop { name, timeout } => ops::stop(&load(&name)?, timeout.map(Duration::from_secs))?,
        Cmd::Status { name } => return ops::status(&load(&name)?),
        Cmd::Logs { name, lines, follow, kiln, run } => ops::logs(&load(&name)?, lines, follow, kiln, run)?,
        Cmd::Update { name, no_start, timeout, dry_run, new_exe, old, no_port, no_gate, no_mark, gate } => {
            let flow = ops::FlowArgs { gate: !no_gate, mark: !no_mark, defs: None, gate_opts: gate.opts() };
            ops::update(&load(&name)?, ops::UpdateArgs {
                no_start, timeout: Duration::from_secs(timeout), dry_run, new_exe, old_exe: old, no_port, flow,
            })?
        }
        Cmd::Port { name, old, new, defs, no_gate, mark, gate } => {
            let flow = ops::FlowArgs { gate: !no_gate, mark, defs, gate_opts: gate.opts() };
            return ops::port_cmd(&load(&name)?, old, new, &flow);
        }
        Cmd::Gate { name, baseline, gate } => return ops::gate_cmd(&load(&name)?, &gate.opts(), baseline),
        Cmd::Regproof {
            name, side, tree, new_exe, old_exe, new_refl, old_refl, record, ids, prev_ids, evidence, game_build, steam_build, out,
            first_build, accept, mark, replace, mirror, server_exe, server_refl,
        } => {
            return regproof::cli(regproof::CliArgs {
                name, side, tree, new_exe, old_exe, new_refl, old_refl, record, ids, prev_ids, evidence, game_build, steam_build, out,
                first_build, accept, mark, replace, mirror, server_exe, server_refl,
            });
        }
        Cmd::Backup { name } => {
            ops::backup(&load(&name)?)?;
        }
        Cmd::List => ops::list()?,
        Cmd::Report { name, dir, out, print, exclude, contents } => return report::run(report::ReportArgs { name, dir, out, print, exclude, contents }),
        Cmd::Bundle { cmd } => {
            return match cmd {
                BundleCmd::Create { stage, out, git, epoch } => bundle::create(&stage, &out, git, epoch),
                BundleCmd::Verify { bundle } => bundle::verify(&bundle),
                BundleCmd::Install { bundle, dir, components, switch_from_shroudforge, real } => {
                    bundle::install_cmd(bundle::InstallArgs { bundle, dir, components, switch_from_shroudforge, real })
                }
                BundleCmd::Uninstall { dir, real } => bundle::uninstall_cmd(&dir, real),
            };
        }
        Cmd::Library { cmd } => {
            return match cmd {
                LibraryCmd::Import { paths } => profiles::import(paths),
                LibraryCmd::List { kind } => profiles::list(kind),
                LibraryCmd::TakeOver { entry } => profiles::take_over(&entry),
                LibraryCmd::ReplaceDll { entry, file, profile, confirm } => profiles::replace_dll(&profile, &entry, &file, confirm),
                LibraryCmd::Rollback { profile, entry } => profiles::rollback(&profile, &entry),
            };
        }
        Cmd::Registry { cmd } => {
            return match cmd {
                RegistryCmd::Status { name, dir } => registry::status(name, dir),
                RegistryCmd::Rollback { name, dir, to, clear, dry_run, real } => {
                    registry::rollback(registry::RollbackArgs { name, dir, to, clear, dry_run, real })
                }
                RegistryCmd::Replay { name, dir, opt_in, opt_out, real } => registry::replay(name, dir, opt_in, opt_out, real),
            };
        }
        Cmd::Profile { cmd } => {
            return match cmd {
                ProfileCmd::Create { name, copy_from } => profiles::create(&name, copy_from),
                ProfileCmd::Add { profile, mods, disabled } => profiles::add(&profile, &mods, disabled),
                ProfileCmd::Show { profile, side } => profiles::show(&profile, &side),
            };
        }
        Cmd::Pack { cmd } => return pack::run(cmd),
        Cmd::Client { cmd } => {
            return match cmd {
                ClientCmd::Status { dir } => client::status(dir),
                ClientCmd::Install { bundle, dir, components, switch_from_shroudforge, real } => {
                    client::install(&bundle, dir, components, switch_from_shroudforge, real)
                }
                ClientCmd::Uninstall { dir, real } => client::uninstall(dir, real),
                ClientCmd::Activate { profile, deactivate, dir, real } => client::activate(profile, deactivate, dir, real),
                ClientCmd::PlayOnce { mode, profile, dir, real } => client::play_once(&mode, profile, dir, real),
            };
        }
        Cmd::TickCompare { before, after, json } => {
            return tick_compare::run(tick_compare::TickCompareArgs { before, after, json });
        }
        Cmd::Cmd { name, line, source, complete, json, timeout } => {
            return console::run(console::CmdArgs {
                target: name,
                line: line.join(" "),
                source,
                complete,
                json,
                timeout: Duration::from_secs(timeout),
            });
        }
    }
    Ok(0)
}

/// What a command does with the launcher's data root (lock.rs), decided once here. Every command is named: a new one has
/// to say whether it uses the root. Whole: `kiln_manage` calls that take the data folder (`bundle::data_dirs()`,
/// `game::inspect`, which locates it for the path mode, and the support report), and `backup`, whose `backup_dir` may
/// lie inside it. None: the instance files (Kiln's config folder), the server folder, the Wine prefix and Kiln's state
/// folder (gate results, the exe cache) only; among them the long runs (`run`, `start`, `stop`, `logs --follow`, `port`,
/// `gate`, `install`, `prefix`), which would otherwise block a launcher's move for hours. Part: `update` runs SteamCMD,
/// the port, the gate and a start (no root), and a savegame backup (the lock for that step only).
fn data_use(cmd: &Cmd) -> lock::Use {
    use lock::Use;
    match cmd {
        Cmd::Init { .. } | Cmd::Install { .. } | Cmd::Prefix { .. } | Cmd::Preflight { .. } | Cmd::Run { .. } => Use::None,
        Cmd::Start { .. } | Cmd::Stop { .. } | Cmd::Status { .. } | Cmd::Logs { .. } | Cmd::Port { .. } | Cmd::Gate { .. } => Use::None,
        Cmd::List | Cmd::Cmd { .. } | Cmd::TickCompare { .. } | Cmd::Regproof { .. } => Use::None,
        // the game folder's kiln/state and kiln/data only (a server's or a client's), never the launcher's data root
        Cmd::Registry { .. } => Use::None,
        Cmd::Deploy { bundle, .. } => if bundle.is_some() { Use::Whole } else { Use::None },
        Cmd::Update { .. } => Use::Part,
        Cmd::Backup { .. } | Cmd::Report { .. } | Cmd::Library { .. } | Cmd::Profile { .. } | Cmd::Client { .. } => Use::Whole,
        Cmd::Pack { cmd } => if pack::uses_data(cmd) { Use::Whole } else { Use::None },
        Cmd::Bundle { cmd } => match cmd {
            BundleCmd::Create { .. } | BundleCmd::Verify { .. } => Use::None,
            BundleCmd::Install { .. } | BundleCmd::Uninstall { .. } => Use::Whole,
        },
    }
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    // (feature sfi: kiln-mode activations run the archive inspector the Kiln installed in each game
    // folder brought, its version and SHA-256 from that install: kiln_manage::sfi_inspection::Installed)
    #[cfg(feature = "sfi")]
    kiln_manage::profile::sfi::use_installed_inspector();
    // the data folder's lock, before the command does anything, for as long as it runs (dropped after dispatch)
    let _held = match data_use(&cli.cmd) {
        lock::Use::Whole => match lock::hold() {
            Ok(h) => Some(h),
            Err(e) => {
                eprintln!("kilnctl: error: {e:#}");
                return ExitCode::from(1);
            }
        },
        lock::Use::None | lock::Use::Part => None,
    };
    match dispatch(cli.cmd) {
        Ok(code) => ExitCode::from(code.clamp(0, 255) as u8),
        Err(e) => {
            eprintln!("kilnctl: error: {e:#}");
            ExitCode::from(1)
        }
    }
}

#[cfg(test)]
mod data_use_tests {
    use super::*;
    use lock::Use;

    fn use_of(args: &[&str]) -> Use {
        let cli = Cli::try_parse_from(std::iter::once("kilnctl").chain(args.iter().copied())).unwrap();
        data_use(&cli.cmd)
    }

    /// The lock for what uses the data root; none for the long runs on the server folder; one step of an update.
    #[test]
    fn commands_hold_the_lock_as_long_as_they_use_the_data_root() {
        for args in [
            &["profile", "create", "p"][..], &["profile", "show", "p"], &["library", "list"], &["library", "import", "x.zip"], &["library", "take-over", "eml:x"],
            &["client", "status"], &["deploy", "s", "--bundle", "k.zip"], &["bundle", "install", "k.zip", "--dir", "/g"],
            &["deploy", "s", "--bundle", "kiln-runtime-0.2.0"], &["bundle", "install", "kiln-runtime-0.2.0", "--dir", "/g"],
            &["client", "install", "kiln-runtime-0.2.0", "--dir", "/g"],
            &["bundle", "uninstall", "--dir", "/g"], &["backup", "s"], &["report"],
            &["pack", "install", "p.kilnpack", "--yes"], &["pack", "update", "p", "p.kilnpack"], &["pack", "build", "p", "--version", "1.0.0"], &["pack", "list"],
            // Pack Studio's pack source commands (they read and write the source's profile)
            &["pack", "init", "dir", "--name", "Ember run", "--profile", "p"], &["pack", "add", "dir", "eml:loot@1.0.0", "mod.zip"],
            &["pack", "capture-settings", "dir", "--take", "a.json:range@client"], &["pack", "lint", "dir"], &["pack", "credits", "dir", "--set", "a=Me,https://x.y"],
            &["pack", "publish", "dir", "--public", "--yes"], &["pack", "build", "./dir"],
        ] {
            assert_eq!(use_of(args), Use::Whole, "{args:?}");
        }
        for args in [
            &["run", "s"][..], &["start", "s"], &["stop", "s"], &["logs", "s", "--follow"], &["port", "s"], &["gate", "s"],
            &["install", "s"], &["prefix", "s"], &["status", "s"], &["list"], &["deploy", "s"], &["bundle", "verify", "k.zip"],
            &["bundle", "verify", "kiln-runtime-0.2.0"],
            &["cmd", "s", "help"], &["tick-compare", "a.jsonl", "b.jsonl"], &["pack", "verify", "p.kilnpack"],
            &["pack", "check", "p.kilnpack"], &["pack", "check", "--index", "i.json", "--files", "f"],
            &["regproof", "s"], &["regproof", "--tree", "k", "--new-exe", "x"],
            &["registry", "status", "s"], &["registry", "rollback", "s"], &["registry", "rollback", "--dir", "/g", "--clear"],
        ] {
            assert_eq!(use_of(args), Use::None, "{args:?}");
        }
        assert_eq!(use_of(&["update", "s"]), Use::Part);
    }
}
