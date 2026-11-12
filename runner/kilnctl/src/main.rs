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
mod preflight;
mod profiles;
mod procs;
mod report;
mod run;
mod server;
mod sigport;
mod steam;
mod util;
mod wine;

use clap::{Args, Parser, Subcommand};
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

#[derive(Parser)]
#[command(name = "kilnctl", version, about = "Run Enshrouded dedicated servers with Kiln under Wine (SteamCMD, no Steam client)")]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

/// The lab gate (a copy of the instance with policy any and Kiln's self tests; docs/kiln/sigport.md)
#[derive(Args, Clone)]
struct GateArgs {
    /// Test mods (mods/tests/<id>), comma separated [default: world, players, chat, items, settings, gameplay,
    /// hooks, combat, spawn, content]
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
        /// Install this runtime bundle (kiln-runtime-*.zip) instead of running deploy-server.sh
        #[arg(long, value_name = "ZIP", conflicts_with_all = ["from", "build"])]
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
}

#[derive(Subcommand)]
enum LibraryCmd {
    /// Import folders, zips or DLLs (Kiln mods, native mods, EMF mods, plugins, bare DLLs)
    Import {
        #[arg(required = true)]
        paths: Vec<PathBuf>,
    },
    /// List the library (optionally one kind: kiln, native, plugin, emf)
    List { kind: Option<String> },
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
    /// Check every file's size and sha256 against kiln-runtime.json (and the zip's entries)
    Verify { zip: PathBuf },
    /// Install or upgrade a bundle into a game folder (client or server, by its exe); writes kiln/.install.json
    Install {
        zip: PathBuf,
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
        zip: PathBuf,
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
            Some(zip) => ops::deploy_bundle(&load(&name)?, &zip, samples)?,
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
        Cmd::Backup { name } => {
            ops::backup(&load(&name)?)?;
        }
        Cmd::List => ops::list()?,
        Cmd::Report { name, dir, out, print } => return report::run(report::ReportArgs { name, dir, out, print }),
        Cmd::Bundle { cmd } => {
            return match cmd {
                BundleCmd::Create { stage, out, git, epoch } => bundle::create(&stage, &out, git, epoch),
                BundleCmd::Verify { zip } => bundle::verify(&zip),
                BundleCmd::Install { zip, dir, components, switch_from_shroudforge, real } => {
                    bundle::install_cmd(bundle::InstallArgs { zip, dir, components, switch_from_shroudforge, real })
                }
                BundleCmd::Uninstall { dir, real } => bundle::uninstall_cmd(&dir, real),
            };
        }
        Cmd::Library { cmd } => {
            return match cmd {
                LibraryCmd::Import { paths } => profiles::import(paths),
                LibraryCmd::List { kind } => profiles::list(kind),
            };
        }
        Cmd::Profile { cmd } => {
            return match cmd {
                ProfileCmd::Create { name, copy_from } => profiles::create(&name, copy_from),
                ProfileCmd::Add { profile, mods, disabled } => profiles::add(&profile, &mods, disabled),
                ProfileCmd::Show { profile, side } => profiles::show(&profile, &side),
            };
        }
        Cmd::Client { cmd } => {
            return match cmd {
                ClientCmd::Status { dir } => client::status(dir),
                ClientCmd::Install { zip, dir, components, switch_from_shroudforge, real } => {
                    client::install(&zip, dir, components, switch_from_shroudforge, real)
                }
                ClientCmd::Uninstall { dir, real } => client::uninstall(dir, real),
                ClientCmd::Activate { profile, deactivate, dir, real } => client::activate(profile, deactivate, dir, real),
                ClientCmd::PlayOnce { mode, profile, dir, real } => client::play_once(&mode, profile, dir, real),
            };
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
        Cmd::List | Cmd::Cmd { .. } => Use::None,
        Cmd::Deploy { bundle, .. } => if bundle.is_some() { Use::Whole } else { Use::None },
        Cmd::Update { .. } => Use::Part,
        Cmd::Backup { .. } | Cmd::Report { .. } | Cmd::Library { .. } | Cmd::Profile { .. } | Cmd::Client { .. } => Use::Whole,
        Cmd::Bundle { cmd } => match cmd {
            BundleCmd::Create { .. } | BundleCmd::Verify { .. } => Use::None,
            BundleCmd::Install { .. } | BundleCmd::Uninstall { .. } => Use::Whole,
        },
    }
}

fn main() -> ExitCode {
    let cli = Cli::parse();
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
            &["profile", "create", "p"][..], &["profile", "show", "p"], &["library", "list"], &["library", "import", "x.zip"],
            &["client", "status"], &["deploy", "s", "--bundle", "k.zip"], &["bundle", "install", "k.zip", "--dir", "/g"],
            &["bundle", "uninstall", "--dir", "/g"], &["backup", "s"], &["report"],
        ] {
            assert_eq!(use_of(args), Use::Whole, "{args:?}");
        }
        for args in [
            &["run", "s"][..], &["start", "s"], &["stop", "s"], &["logs", "s", "--follow"], &["port", "s"], &["gate", "s"],
            &["install", "s"], &["prefix", "s"], &["status", "s"], &["list"], &["deploy", "s"], &["bundle", "verify", "k.zip"],
            &["cmd", "s", "help"],
        ] {
            assert_eq!(use_of(args), Use::None, "{args:?}");
        }
        assert_eq!(use_of(&["update", "s"]), Use::Part);
    }
}
